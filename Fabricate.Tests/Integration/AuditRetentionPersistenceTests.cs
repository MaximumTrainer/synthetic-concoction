using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Fabricate.Tests.Integration;

/// <summary>
/// #74: the retention purge is a provider-level DELETE, so the boundary has to hold where it actually runs, not
/// just against the in-memory adapter. Both legs migrate a real database and assert the same window: an event at
/// 31 days goes, an event at 29 days stays.
/// </summary>
public sealed class AuditRetentionPersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private PostgreSqlContainer? _container;
    private string? _connectionString;
    private readonly string _sqliteFile = Path.Combine(Path.GetTempPath(), $"fabricate-audit-{Guid.NewGuid():N}.db");

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
            _container = null; // No Docker: the PostgreSQL leg self-skips.
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_sqliteFile)) File.Delete(_sqliteFile);
    }

    [Fact]
    public async Task Sqlite_PurgesBeyondTheWindowAndKeepsWhatIsInsideIt()
    {
        var options = new DbContextOptionsBuilder<FabricateDbContext>()
            .UseSqlite($"Data Source={_sqliteFile}")
            .Options;

        await using var db = new FabricateDbContext(options);
        await db.Database.MigrateAsync();

        await AssertRetentionWindowAsync(db);
    }

    [Fact]
    public async Task PostgreSql_PurgesBeyondTheWindowAndKeepsWhatIsInsideIt()
    {
        if (_connectionString is null) return;

        var name = $"fab_{Guid.NewGuid():N}";
        await using (var admin = NewPostgresContext(_connectionString))
        {
            // CREATE DATABASE cannot be parameterised; the identifier is a hex GUID generated here, not input.
#pragma warning disable EF1002
            await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE \"{name}\"");
#pragma warning restore EF1002
        }

        await using var db = NewPostgresContext(
            _connectionString.Replace("Database=postgres", $"Database={name}", StringComparison.OrdinalIgnoreCase));
        await db.Database.MigrateAsync();

        await AssertRetentionWindowAsync(db);
    }

    private static FabricatePostgresDbContext NewPostgresContext(string connectionString)
        => new(new DbContextOptionsBuilder<FabricatePostgresDbContext>().UseNpgsql(connectionString).Options);

    private static async Task AssertRetentionWindowAsync(FabricateDbContext db)
    {
        var repository = new EfAuditLogRepository(db);
        var accountId = Guid.NewGuid();

        await repository.AppendAsync(Event(accountId, "old.event", Now.AddDays(-31)));
        await repository.AppendAsync(Event(accountId, "recent.event", Now.AddDays(-29)));
        await repository.AppendAsync(Event(accountId, "boundary.event", Now.AddDays(-30)));

        var cutoff = Now.AddDays(-30);
        var deleted = await repository.DeleteOlderThanAsync(cutoff, batchSize: 1_000);

        deleted.Should().Be(1, "only the 31-day-old event is beyond a 30-day window");

        var remaining = await db.AuditEvents.AsNoTracking().Select(e => e.Action).ToListAsync();
        remaining.Should().BeEquivalentTo(["recent.event", "boundary.event"],
            "an event exactly on the boundary is kept; the purge deletes what is strictly older");
    }

    [Fact]
    public async Task Sqlite_DeletesInBatchesAndStreamsTheExportInOrder()
    {
        var file = Path.Combine(Path.GetTempPath(), $"fabricate-audit-batch-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<FabricateDbContext>().UseSqlite($"Data Source={file}").Options;
            await using var db = new FabricateDbContext(options);
            await db.Database.MigrateAsync();

            var repository = new EfAuditLogRepository(db);
            var accountId = Guid.NewGuid();
            for (var i = 0; i < 25; i++)
            {
                await repository.AppendAsync(Event(accountId, $"old.{i:D2}", Now.AddDays(-40).AddMinutes(i)));
            }
            await repository.AppendAsync(Event(accountId, "kept", Now.AddDays(-1)));

            // Export streams the account's events oldest first, before anything is purged.
            var exported = new List<string>();
            await foreach (var e in repository.StreamAsync(accountId, null, null)) exported.Add(e.Action);
            exported.Should().HaveCount(26);
            exported[0].Should().Be("old.00");
            exported[^1].Should().Be("kept");

            // A batch smaller than the backlog deletes exactly one batch per call.
            var first = await repository.DeleteOlderThanAsync(Now.AddDays(-30), batchSize: 10);
            first.Should().Be(10, "the purge must honour the batch size rather than deleting the whole backlog at once");

            var second = await repository.DeleteOlderThanAsync(Now.AddDays(-30), batchSize: 10);
            var third = await repository.DeleteOlderThanAsync(Now.AddDays(-30), batchSize: 10);
            (second + third).Should().Be(15);

            var remaining = await db.AuditEvents.AsNoTracking().Select(e => e.Action).ToListAsync();
            remaining.Should().Equal(["kept"]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private static AuditEvent Event(Guid accountId, string action, DateTimeOffset occurredAt)
        => new(Guid.NewGuid(), accountId, null, action, "Thing", "1", "corr", occurredAt);
}
