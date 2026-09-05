using System.Security.Cryptography;
using Fabricate.Application.Abstractions;
using Fabricate.Application.Generation;
using Fabricate.Application.Schema;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Export;
using Fabricate.Infrastructure.Persistence;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Fabricate.Tests.Integration;

/// <summary>
/// #75: the snapshot services existed but nothing reached them, and they kept their state in dictionaries — so a
/// snapshot did not survive a restart and was not really workspace-scoped. These cover storage, scoping, and the
/// point of the whole thing: a run reproducible from a stored snapshot without touching the source database.
/// </summary>
public sealed class SnapshotTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _connectionString;
    private readonly string _sqliteFile = Path.Combine(Path.GetTempPath(), $"fabricate-snap-{Guid.NewGuid():N}.db");
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fabricate-snap-out-{Guid.NewGuid():N}");

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("FABRICATE_SKIP_DOCKER_TESTS") == "1") return;

        try
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception)
        {
            _container = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_sqliteFile)) File.Delete(_sqliteFile);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ── scoping and versioning, over the in-memory adapter ───────────────────────

    [Fact]
    public async Task SnapshotsAreWorkspaceScoped_AndACrossWorkspaceIdIsNotFound()
    {
        var service = new SchemaSnapshotService(new InMemorySchemaSnapshotRepository());
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();

        var snapshot = await service.SaveSnapshotAsync(mine, GenerationFixture.Schema);

        (await service.GetSnapshotAsync(mine, snapshot.Id)).Should().NotBeNull();
        (await service.GetSnapshotAsync(theirs, snapshot.Id)).Should().BeNull(
            "a stored schema describes a customer's database, so its id must not be an existence oracle across tenants");
        (await service.RestoreSchemaAsync(theirs, snapshot.Id)).Should().BeNull();
        (await service.ListSnapshotsAsync(theirs)).Should().BeEmpty();
    }

    [Fact]
    public async Task VersionsIncrementPerWorkspace_Independently()
    {
        var service = new SchemaSnapshotService(new InMemorySchemaSnapshotRepository());
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        (await service.SaveSnapshotAsync(first, GenerationFixture.Schema)).Version.Should().Be(1);
        (await service.SaveSnapshotAsync(first, GenerationFixture.Schema)).Version.Should().Be(2);
        (await service.SaveSnapshotAsync(second, GenerationFixture.Schema)).Version.Should().Be(1,
            "a new workspace starts at version 1 regardless of what other workspaces have taken");
    }

    [Fact]
    public async Task ListingReturnsNewestFirst()
    {
        var service = new SchemaSnapshotService(new InMemorySchemaSnapshotRepository());
        var workspaceId = Guid.NewGuid();

        await service.SaveSnapshotAsync(workspaceId, GenerationFixture.Schema);
        await service.SaveSnapshotAsync(workspaceId, GenerationFixture.Schema);

        (await service.ListSnapshotsAsync(workspaceId)).Select(s => s.Version).Should().Equal([2, 1]);
    }

    // ── the point: reproducing a run from a stored snapshot ──────────────────────

    [Fact]
    public async Task TwoRunsFromTheSameStoredSnapshotAndSeed_ProduceByteIdenticalExports()
    {
        var service = new SchemaSnapshotService(new InMemorySchemaSnapshotRepository());
        var workspaceId = Guid.NewGuid();
        var snapshot = await service.SaveSnapshotAsync(workspaceId, GenerationFixture.Schema);

        var first = await GenerateFromSnapshotAsync(service, workspaceId, snapshot.Id, "run-a");
        var second = await GenerateFromSnapshotAsync(service, workspaceId, snapshot.Id, "run-b");

        first.Should().NotBeEmpty();
        second.Should().BeEquivalentTo(first,
            "a snapshot plus a seed is the whole input, so two runs from it must agree byte for byte");
    }

    private async Task<Dictionary<string, string>> GenerateFromSnapshotAsync(
        ISchemaSnapshotService service,
        Guid workspaceId,
        Guid snapshotId,
        string label)
    {
        // Restored from storage — nothing here reconnects to the database the schema came from.
        var schema = await service.RestoreSchemaAsync(workspaceId, snapshotId);
        schema.Should().NotBeNull();

        var target = Path.Combine(_root, label);
        var request = new GenerationRequest(
            schema!,
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["main.users"] = 20,
                ["main.orders"] = 20,
                ["main.order_items"] = 20,
            },
            Seed: 4242);

        var (result, _) = await GenerationFixture.CreateOrchestrator(4242).GenerateAsync(request);
        await new CsvExporter().ExportAsync(result.Tables, target);

        return Directory.GetFiles(target, "*.csv")
            .ToDictionary(
                f => Path.GetFileName(f),
                f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))),
                StringComparer.Ordinal);
    }

    [Fact]
    public async Task APlanCanBeBuiltFromAStoredSnapshotWithoutReconnecting()
    {
        var service = new SchemaSnapshotService(new InMemorySchemaSnapshotRepository());
        var workspaceId = Guid.NewGuid();
        var snapshot = await service.SaveSnapshotAsync(workspaceId, GenerationFixture.Schema);

        var schema = await service.RestoreSchemaAsync(workspaceId, snapshot.Id);
        IGeneratorRegistry registry = new GeneratorRegistry();
        registry.RegisterDefaults(new DeterministicRandomService(1));
        var plan = new GenerationPlanService(registry).BuildDiagnosticsReport(schema!);

        plan.Columns.Should().NotBeEmpty();
        plan.Columns.Select(c => c.Table).Should().Contain("main.users");
        plan.Columns.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.StrategyProvenance),
            "the point of reviewing a plan is seeing why each column resolved as it did");
    }

    // ── persistence, both providers ─────────────────────────────────────────────

    [Fact]
    public async Task Sqlite_RoundTripsBothSnapshotKinds()
    {
        var options = new DbContextOptionsBuilder<FabricateDbContext>().UseSqlite($"Data Source={_sqliteFile}").Options;
        await using var db = new FabricateDbContext(options);
        await db.Database.MigrateAsync();

        await AssertRoundTripAsync(new EfSchemaSnapshotRepository(db), new EfProfileSnapshotRepository(db));
    }

    [Fact]
    public async Task PostgreSql_RoundTripsBothSnapshotKinds()
    {
        if (_connectionString is null) return;

        var name = $"fab_{Guid.NewGuid():N}";
        await using (var admin = NewPostgresContext(_connectionString))
        {
#pragma warning disable EF1002
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE \"{name}\"");
#pragma warning restore EF1002
        }

        await using var db = NewPostgresContext(
            _connectionString.Replace("Database=postgres", $"Database={name}", StringComparison.OrdinalIgnoreCase));
        await db.Database.MigrateAsync();

        await AssertRoundTripAsync(new EfSchemaSnapshotRepository(db), new EfProfileSnapshotRepository(db));
    }

    private static FabricatePostgresDbContext NewPostgresContext(string connectionString)
        => new(new DbContextOptionsBuilder<FabricatePostgresDbContext>().UseNpgsql(connectionString).Options);

    private static async Task AssertRoundTripAsync(ISchemaSnapshotRepository schemaRepo, IProfileSnapshotRepository profileRepo)
    {
        var workspaceId = Guid.NewGuid();
        var otherWorkspace = Guid.NewGuid();

        var schemaService = new SchemaSnapshotService(schemaRepo);
        var stored = await schemaService.SaveSnapshotAsync(workspaceId, GenerationFixture.Schema);

        // The schema survives serialisation whole — tables, columns, keys and constraints.
        var restored = await schemaService.RestoreSchemaAsync(workspaceId, stored.Id);
        restored.Should().NotBeNull();
        restored!.Tables.Should().HaveCount(GenerationFixture.Schema.Tables.Count);

        var users = restored.Tables.Single(t => t.QualifiedName == "main.users");
        users.Columns.Select(c => c.Name).Should().Contain(["id", "email", "manager_id"]);
        users.PrimaryKey.Should().Equal(["id"]);
        users.ForeignKeys.Should().ContainSingle().Which.ReferencedTable.Should().Be("main.users");
        users.UniqueConstraints.Should().ContainSingle().Which.Columns.Should().Equal(["email"]);

        (await schemaService.GetSnapshotAsync(otherWorkspace, stored.Id)).Should().BeNull();

        var profileService = new ProfileSnapshotService(profileRepo);
        var profile = await profileService.SaveProfileAsync(workspaceId, new ProfileSnapshot(
            Guid.Empty, "fixture", 0, DateTimeOffset.UtcNow,
            [new TableProfile("main.users", 100, [new ColumnProfile("email", 100, 0, 100, "a@example.com", "z@example.com", null)])]));

        profile.Version.Should().Be(1);
        profile.WorkspaceId.Should().Be(workspaceId);

        var readBack = await profileService.GetProfileAsync(workspaceId, profile.Id);
        readBack!.Tables.Should().ContainSingle().Which.RowCount.Should().Be(100);
        (await profileService.GetProfileAsync(otherWorkspace, profile.Id)).Should().BeNull();
        (await profileService.ListProfilesAsync(otherWorkspace)).Should().BeEmpty();
    }
}
