using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Fabricate.Tests.Integration;

/// <summary>
/// #77: the budget check sums tokens in the database on every turn and the rollups read a windowed set, so both
/// have to work where they actually run rather than only against the in-memory adapter.
/// </summary>
public sealed class LlmUsagePersistenceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private PostgreSqlContainer? _container;
    private string? _connectionString;
    private readonly string _sqliteFile = Path.Combine(Path.GetTempPath(), $"fabricate-usage-{Guid.NewGuid():N}.db");

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
    public async Task Sqlite_RecordsRollUpAndTotal()
    {
        var options = new DbContextOptionsBuilder<FabricateDbContext>().UseSqlite($"Data Source={_sqliteFile}").Options;
        await using var db = new FabricateDbContext(options);
        await db.Database.MigrateAsync();

        await AssertRollupAsync(new EfLlmUsageRepository(db));
    }

    [Fact]
    public async Task PostgreSql_RecordsRollUpAndTotal()
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

        await AssertRollupAsync(new EfLlmUsageRepository(db));
    }

    private static FabricatePostgresDbContext NewPostgresContext(string connectionString)
        => new(new DbContextOptionsBuilder<FabricatePostgresDbContext>().UseNpgsql(connectionString).Options);

    private static async Task AssertRollupAsync(ILlmUsageRepository repository)
    {
        var workspace = Guid.NewGuid();
        var other = Guid.NewGuid();
        var credential = Guid.NewGuid();

        await repository.RecordAsync(Record(workspace, "claude-opus-5", 100, 50, credential, Now.AddHours(-1)));
        await repository.RecordAsync(Record(workspace, "claude-opus-5", 200, 25, credential, Now.AddHours(-2)));
        await repository.RecordAsync(Record(workspace, "gpt-x", 10, 5, null, Now.AddHours(-3)));
        await repository.RecordAsync(Record(workspace, "claude-opus-5", 9_999, 0, credential, Now.AddDays(-90)));
        await repository.RecordAsync(Record(other, "claude-opus-5", 5_000, 0, credential, Now.AddHours(-1)));

        var from = Now.AddDays(-7);
        var to = Now;

        var byModel = await repository.SummariseWorkspaceAsync(workspace, from, to, LlmUsageGrouping.Model);
        byModel.TotalTokens.Should().Be(390, "the window and the workspace both have to bind");
        byModel.Calls.Should().Be(3);
        byModel.Buckets.Single(b => b.Key == "claude-opus-5").TotalTokens.Should().Be(375);
        byModel.Buckets.Single(b => b.Key == "gpt-x").TotalTokens.Should().Be(15);

        var byCredential = await repository.SummariseWorkspaceAsync(workspace, from, to, LlmUsageGrouping.Credential);
        byCredential.Buckets.Single(b => b.Key == credential.ToString()).TotalTokens.Should().Be(375);
        byCredential.Buckets.Single(b => b.Key == "platform").TotalTokens.Should().Be(15);

        var byDay = await repository.SummariseWorkspaceAsync(workspace, from, to, LlmUsageGrouping.Day);
        byDay.Buckets.Should().ContainSingle().Which.Key.Should().Be("2026-09-05");

        // The budget check sums in the database rather than reading the window into memory.
        (await repository.TotalTokensAsync(workspace, from, to)).Should().Be(390);
        (await repository.TotalTokensAsync(Guid.NewGuid(), from, to)).Should().Be(0,
            "a workspace with no rows must total zero, not fail");

        var account = await repository.SummariseWorkspacesAsync([workspace, other], from, to, LlmUsageGrouping.Model);
        account.TotalTokens.Should().Be(5_390, "the account rollup spans every workspace given");
    }

    private static LlmUsageRecord Record(Guid workspaceId, string model, long input, long output, Guid? credentialId, DateTimeOffset at)
        => new(Guid.NewGuid(), workspaceId, null, null, credentialId, "anthropic", model, input, output, 1, 120, LlmCallOutcome.Success, at);
}
