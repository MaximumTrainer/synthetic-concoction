using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;

namespace Fabricate.Tests.Integration;

/// <summary>
/// The PostgreSQL provider against a real server (Testcontainers). Skips itself when no Docker daemon is reachable,
/// so the suite stays green on machines without Docker; CI runs it on ubuntu-latest.
/// </summary>
public sealed class PostgresPersistenceIntegrationTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("FABRICATE_SKIP_DOCKER_TESTS") == "1")
            return;

        try
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception)
        {
            // No Docker (or it is not running): every test below becomes a no-op.
            _container = null;
            _connectionString = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    private FabricatePostgresDbContext NewContext(string? database = null)
    {
        var cs = _connectionString!;
        if (database is not null) cs = cs.Replace("Database=postgres", $"Database={database}", StringComparison.OrdinalIgnoreCase);
        var options = new DbContextOptionsBuilder<FabricatePostgresDbContext>().UseNpgsql(cs).Options;
        return new FabricatePostgresDbContext(options);
    }

    private async Task<string> CreateEmptyDatabaseAsync()
    {
        var name = $"fab_{Guid.NewGuid():N}";
        await using var admin = NewContext();
        // CREATE DATABASE cannot take a parameter; the identifier is a hex GUID generated above, not external input.
#pragma warning disable EF1002
        await admin.Database.ExecuteSqlRawAsync($"CREATE DATABASE \"{name}\"");
#pragma warning restore EF1002
        return name;
    }

    [Fact]
    public async Task MigrateOnEmptyDatabase_CreatesSchema_AndRepositoriesWork()
    {
        if (_connectionString is null) return;

        var dbName = await CreateEmptyDatabaseAsync();
        await using var db = NewContext(dbName);

        (await db.Database.GetPendingMigrationsAsync()).Should().NotBeEmpty("a fresh database has everything pending");
        await db.Database.MigrateAsync();
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

        var accounts = new EfAccountRepository(db);
        var account = new Account(Guid.NewGuid(), "Acme", DateTimeOffset.UtcNow);
        await accounts.SaveAsync(account);
        (await accounts.GetByIdAsync(account.Id))!.Name.Should().Be("Acme");

        var sessions = new EfSessionRepository(db);
        var session = new ChatSession(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), "S", ChatMode.Guided, false, DateTimeOffset.UtcNow);
        await sessions.SaveAsync(session);
        var first = new ChatMessage(Guid.NewGuid(), session.Id, MessageRole.User, "one", DateTimeOffset.UtcNow.AddSeconds(-2));
        var second = new ChatMessage(Guid.NewGuid(), session.Id, MessageRole.Assistant, "two", DateTimeOffset.UtcNow);
        await sessions.SaveMessageAsync(second);
        await sessions.SaveMessageAsync(first);
        (await sessions.GetMessagesAsync(session.Id, 0, 10)).Select(m => m.Content).Should().Equal("one", "two");

        var credentials = new EfLlmCredentialStore(db);
        var ws = Guid.NewGuid();
        await credentials.SaveAsync(new LlmCredential(Guid.NewGuid(), ws, null, "k", LlmProvider.Anthropic, LlmCredentialKind.ApiKey,
            "CT", "dp-v1", "fp", "1234", null, "claude-opus-5", new Dictionary<string, string>(), true, LlmCredentialStatus.Active,
            DateTimeOffset.UtcNow, Guid.NewGuid()));
        (await credentials.ListByWorkspaceAsync(ws)).Should().ContainSingle();
    }

    [Fact]
    public async Task ConcurrentMigrations_ApplySchemaExactlyOnce()
    {
        if (_connectionString is null) return;

        var dbName = await CreateEmptyDatabaseAsync();

        // Two instances starting together, as on a scale-out deploy. EF Core 9's migration lock serialises them.
        var tasks = Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var db = NewContext(dbName);
            await db.Database.MigrateAsync();
        });
        await Task.WhenAll(tasks);

        await using var check = NewContext(dbName);
        (await check.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
        (await check.Database.GetAppliedMigrationsAsync()).Should().NotBeEmpty();
        (await check.Accounts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task HostedMigrationService_RunsBeforeSeedingCanTouchTheDatabase()
    {
        if (_connectionString is null) return;

        var dbName = await CreateEmptyDatabaseAsync();
        var cs = _connectionString!.Replace("Database=postgres", $"Database={dbName}", StringComparison.OrdinalIgnoreCase);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFabricatePersistence("postgres", cs);
        await using var provider = services.BuildServiceProvider();

        var migrator = new DatabaseMigrationHostedService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<DatabaseMigrationHostedService>.Instance);
        await migrator.StartAsync(CancellationToken.None);

        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FabricateDbContext>();
        db.Should().BeOfType<FabricatePostgresDbContext>("the base type must resolve to the PostgreSQL context");
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task FilteredUniqueIndex_AllowsReusingARevokedName()
    {
        if (_connectionString is null) return;

        var dbName = await CreateEmptyDatabaseAsync();
        await using var db = NewContext(dbName);
        await db.Database.MigrateAsync();
        var store = new EfLlmCredentialStore(db);
        var ws = Guid.NewGuid();

        LlmCredential Cred(DateTimeOffset? revoked) => new(Guid.NewGuid(), ws, null, "primary", LlmProvider.Anthropic, LlmCredentialKind.ApiKey,
            "CT", "dp-v1", "fp", "1234", null, "claude-opus-5", new Dictionary<string, string>(), false,
            revoked is null ? LlmCredentialStatus.Active : LlmCredentialStatus.Revoked, DateTimeOffset.UtcNow, Guid.NewGuid(), RevokedAt: revoked);

        await store.SaveAsync(Cred(DateTimeOffset.UtcNow));
        await store.SaveAsync(Cred(null));
        var duplicate = () => store.SaveAsync(Cred(null));

        await duplicate.Should().ThrowAsync<DbUpdateException>();
    }
}
