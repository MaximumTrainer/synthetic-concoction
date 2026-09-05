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

    /// <summary>#65: the aggregates that used to live in service fields must survive a process restart.</summary>
    [Fact]
    public async Task PlatformAggregates_SurviveAcrossContexts()
    {
        if (_connectionString is null) return;

        var dbName = await CreateEmptyDatabaseAsync();
        var workspaceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        // First "process": write through the repositories.
        await using (var db = NewContext(dbName))
        {
            await db.Database.MigrateAsync();

            var workspaces = new EfWorkspaceRepository(db);
            await workspaces.SaveAsync(new Workspace(workspaceId, Guid.NewGuid(), "Engineering", DateTimeOffset.UtcNow));
            await workspaces.SaveMembershipAsync(new WorkspaceMembership(workspaceId, userId, false, WorkspaceRole.Admin, DateTimeOffset.UtcNow));

            await new EfConnectionRepository(db).SaveAsync(
                new Connection(Guid.NewGuid(), workspaceId, "warehouse", "postgres", "active", DateTimeOffset.UtcNow));

            await new EfInstructionVersionRepository(db).SaveAsync(
                new InstructionVersion(Guid.NewGuid(), workspaceId, 1, "Always answer in French.", userId, DateTimeOffset.UtcNow));
            await new EfInstructionVersionRepository(db).SaveAsync(
                new InstructionVersion(Guid.NewGuid(), Guid.Empty, 1, "Project rules.", userId, DateTimeOffset.UtcNow, projectId));

            await new EfProjectRepository(db).SaveAsync(
                new Project(projectId, workspaceId, "Customer data", ProjectStatus.Active, userId, DateTimeOffset.UtcNow));
            await new EfProjectDatabaseRepository(db).SaveAsync(
                new ProjectDatabase(Guid.NewGuid(), projectId, "primary", ProjectDatabaseType.External, "postgres", "active", null, DateTimeOffset.UtcNow));

            var workflows = new EfWorkflowRepository(db);
            await workflows.SaveAsync(new Workflow(workflowId, workspaceId, "Nightly", 1, WorkflowStatus.Active, DateTimeOffset.UtcNow));
            var step = new WorkflowStep(Guid.NewGuid(), workflowId, 1, "generate", null);
            await workflows.SaveStepAsync(step);
            var run = new WorkflowRun(Guid.NewGuid(), workflowId, WorkflowRunStatus.Queued, DateTimeOffset.UtcNow);
            await workflows.SaveRunAsync(run);
            await workflows.SaveStepRunAsync(new WorkflowStepRun(Guid.NewGuid(), run.Id, step.Id, 1, WorkflowRunStatus.Queued, 0));

            await new EfSkillRepository(db).SaveAsync(
                new Skill(Guid.NewGuid(), workspaceId, "reporting", "Reporting skill", ["discover_schema"], true, DateTimeOffset.UtcNow));

            var groupId = Guid.NewGuid();
            var groups = new EfAccountGroupRepository(db);
            await groups.SaveAsync(new AccountGroup(groupId, workspaceId, "Engineers", DateTimeOffset.UtcNow));
            await groups.AddMemberAsync(new GroupMembership(groupId, userId, DateTimeOffset.UtcNow));

            await new EfAllowedDomainRepository(db).SaveAsync(
                new AllowedDomain(Guid.NewGuid(), workspaceId, "example.com", DateTimeOffset.UtcNow));

            await new EfWebhookRepository(db).SaveAsync(
                new WebhookRegistration(Guid.NewGuid(), workspaceId, "https://hooks.example.com/x", ["run.completed"], "s3cret", true, DateTimeOffset.UtcNow));
        }

        // Second "process": a fresh context over the same database reads everything back.
        await using var reopened = NewContext(dbName);

        (await new EfWorkspaceRepository(reopened).GetByIdAsync(workspaceId))!.Name.Should().Be("Engineering");
        (await new EfWorkspaceRepository(reopened).ListMembershipsAsync(workspaceId)).Should().ContainSingle(m => m.PrincipalId == userId);
        (await new EfConnectionRepository(reopened).ListByWorkspaceAsync(workspaceId)).Should().ContainSingle();
        (await new EfInstructionVersionRepository(reopened).ListByWorkspaceAsync(workspaceId)).Should().ContainSingle();
        (await new EfInstructionVersionRepository(reopened).ListByProjectAsync(projectId)).Should().ContainSingle();
        (await new EfProjectRepository(reopened).ListByWorkspaceAsync(workspaceId)).Should().ContainSingle();
        (await new EfProjectDatabaseRepository(reopened).ListByProjectAsync(projectId)).Should().ContainSingle();

        var reopenedWorkflows = new EfWorkflowRepository(reopened);
        (await reopenedWorkflows.ListByWorkspaceAsync(workspaceId)).Should().ContainSingle();
        (await reopenedWorkflows.ListStepsAsync(workflowId)).Should().ContainSingle();

        var skills = await new EfSkillRepository(reopened).ListByWorkspaceAsync(workspaceId);
        skills.Should().ContainSingle().Which.AllowedTools.Should().Equal("discover_schema");

        (await new EfAccountGroupRepository(reopened).ListGroupIdsForUserAsync(userId)).Should().ContainSingle();
        (await new EfAllowedDomainRepository(reopened).ListByAccountAsync(workspaceId)).Should().ContainSingle();

        var webhooks = await new EfWebhookRepository(reopened).ListByWorkspaceAsync(workspaceId);
        webhooks.Should().ContainSingle().Which.Events.Should().Equal("run.completed");
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
