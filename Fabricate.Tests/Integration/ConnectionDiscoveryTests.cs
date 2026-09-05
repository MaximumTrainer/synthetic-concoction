using Fabricate.Application.Abstractions;
using Fabricate.Application.Governance;
using Fabricate.Application.Workspaces;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using Fabricate.Infrastructure.Schema;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Fabricate.Tests.Integration;

/// <summary>
/// #69: connections carried a name and a provider but no connection string, and discovery always used the single
/// instance-level provider — so every chat session introspected the operator's own database whatever workspace it
/// belonged to. These prove a session discovers <em>its</em> database, against a real SQLite file and a real
/// PostgreSQL, and that the connection string reaches no response, audit record or error message.
/// </summary>
public sealed class ConnectionDiscoveryTests : IAsyncLifetime
{
    private const string SqlitePassword = "hunter2-do-not-leak";

    private readonly string _sqliteFile = Path.Combine(Path.GetTempPath(), $"fabricate-conn-{Guid.NewGuid():N}.db");
    private PostgreSqlContainer? _container;
    private string? _postgresConnectionString;

    private readonly InMemoryConnectionRepository _connections = new();
    private readonly InMemoryProjectDatabaseRepository _projectDatabases = new();
    private readonly InMemoryWorkspaceRepository _workspaceRepo = new();
    private readonly InMemoryAuditLogRepository _auditRepo = new();
    private readonly TestServices.PassthroughCipher _cipher = new();
    private readonly SchemaProviderFactory _providerFactory = new();

    private WorkspaceService _workspaces = null!;
    private ConnectionCatalogService _catalog = null!;
    private ConnectionResolver _resolver = null!;

    private Guid _workspaceId;
    private Guid _otherWorkspaceId;
    private Guid _editorId;

    public async Task InitializeAsync()
    {
        // A real SQLite database with a table the instance-level provider does not have.
        await using (var connection = new SqliteConnection($"Data Source={_sqliteFile}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE tenant_widgets (
                  id INTEGER PRIMARY KEY,
                  label TEXT NOT NULL,
                  owner_id INTEGER NULL REFERENCES tenant_widgets(id)
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        var audit = new AuditLogService(_auditRepo, new InMemoryAccountRepository());
        _workspaces = new WorkspaceService(_workspaceRepo, new InMemoryAccountGroupRepository(), audit);
        _catalog = new ConnectionCatalogService(_connections, _workspaces, _cipher, _providerFactory, audit);
        _resolver = new ConnectionResolver(_connections, _projectDatabases, _cipher, _providerFactory);

        _editorId = Guid.NewGuid();
        _workspaceId = (await _workspaces.CreateAsync(new CreateWorkspaceCommand(Guid.NewGuid(), "mine", _editorId))).Id;
        _otherWorkspaceId = (await _workspaces.CreateAsync(new CreateWorkspaceCommand(Guid.NewGuid(), "theirs", Guid.NewGuid()))).Id;

        if (Environment.GetEnvironmentVariable("FABRICATE_SKIP_DOCKER_TESTS") == "1") return;

        try
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await _container.StartAsync();
            _postgresConnectionString = _container.GetConnectionString();

            await using var connection = new NpgsqlConnection(_postgresConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "CREATE TABLE tenant_orders (id BIGINT PRIMARY KEY, reference TEXT NOT NULL UNIQUE);", connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception)
        {
            _container = null;
            _postgresConnectionString = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_sqliteFile)) File.Delete(_sqliteFile);
    }

    private string SqliteConnectionString => $"Data Source={_sqliteFile};Password={SqlitePassword}";

    // ── secrets ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheConnectionStringIsAcceptedOnceAndNeverReturned()
    {
        var created = await _catalog.AddConnectionAsync(_workspaceId, "primary", "sqlite", _editorId, SqliteConnectionString);

        created.HasSecret.Should().BeTrue();
        created.Fingerprint.Should().NotBeNullOrWhiteSpace();
        created.Redacted.Should().Contain("Data Source=", "the target must stay recognisable");
        created.Redacted.Should().NotContain(SqlitePassword);

        var listed = await _catalog.ListAsync(_workspaceId, _editorId);
        var fetched = await _catalog.GetAsync(_workspaceId, created.Id, _editorId);

        foreach (var view in new[] { System.Text.Json.JsonSerializer.Serialize(listed), System.Text.Json.JsonSerializer.Serialize(fetched) })
        {
            view.Should().NotContain(SqlitePassword, "no read path may return the connection string");
            view.Should().NotContain("cipherText", "the stored ciphertext is not a response field either");
        }
    }

    [Fact]
    public async Task TheConnectionStringReachesNoAuditRecord()
    {
        var created = await _catalog.AddConnectionAsync(_workspaceId, "primary", "sqlite", _editorId, SqliteConnectionString);
        await _catalog.RotateAsync(_workspaceId, created.Id, SqliteConnectionString + ";Extra=1", _editorId);
        await _catalog.ValidateAsync(_workspaceId, created.Id, _editorId);

        var everything = string.Join("\n", _auditRepo.All.Select(e => $"{e.Action}|{e.TargetId}|{e.Details}"));

        everything.Should().Contain("connection.created");
        everything.Should().Contain("connection.rotated");
        everything.Should().NotContain(SqlitePassword,
            "an audit record is exported and kept for months; a password in one outlives the connection");
        everything.Should().Contain("fingerprint=", "the fingerprint is what makes a rotation visible");
    }

    [Fact]
    public async Task RotatingChangesTheFingerprint()
    {
        var created = await _catalog.AddConnectionAsync(_workspaceId, "primary", "sqlite", _editorId, SqliteConnectionString);
        var rotated = await _catalog.RotateAsync(_workspaceId, created.Id, SqliteConnectionString + ";Cache=Shared", _editorId);

        rotated.Fingerprint.Should().NotBe(created.Fingerprint);
        rotated.LastValidatedAt.Should().BeNull("a rotated connection has not been validated yet");
    }

    [Fact]
    public async Task ValidationReportsUnreachabilityWithoutLeakingTheConnectionString()
    {
        const string secret = "Host=nonexistent.invalid;Username=app;Password=super-secret-value;Database=prod";
        var created = await _catalog.AddConnectionAsync(_workspaceId, "broken", "postgres", _editorId, secret);

        var result = await _catalog.ValidateAsync(_workspaceId, created.Id, _editorId);

        result.IsReachable.Should().BeFalse();
        result.Message.Should().NotBeNullOrWhiteSpace("an unreachable connection must say something useful");
        result.Message.Should().NotContain("super-secret-value",
            "drivers quote the connection string back in their messages more often than not");

        var everything = string.Join("\n", _auditRepo.All.Select(e => e.Details));
        everything.Should().NotContain("super-secret-value");
    }

    [Fact]
    public async Task AnUnsupportedProviderIsRejected()
    {
        var add = async () => await _catalog.AddConnectionAsync(_workspaceId, "weird", "cassandra", _editorId, "x");

        (await add.Should().ThrowAsync<ArgumentException>()).WithMessage("*Supported:*");
    }

    // ── scoping ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectionsAreWorkspaceScoped()
    {
        var created = await _catalog.AddConnectionAsync(_workspaceId, "primary", "sqlite", _editorId, SqliteConnectionString);

        (await _catalog.GetAsync(_otherWorkspaceId, created.Id, _editorId)).Should().BeNull();
        (await _catalog.ListAsync(_workspaceId, _editorId)).Should().ContainSingle();

        var rotateElsewhere = async () => await _catalog.RotateAsync(_otherWorkspaceId, created.Id, "x", _editorId);
        await rotateElsewhere.Should().ThrowAsync<Exception>(
            "a connection id from another workspace must not be actionable");
    }

    // ── discovery ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASessionWithAWorkspaceConnectionDiscoversThatDatabase()
    {
        await _catalog.AddConnectionAsync(_workspaceId, "primary", "sqlite", _editorId, $"Data Source={_sqliteFile}");

        var provider = await _resolver.ResolveAsync(_workspaceId, projectId: null);

        provider.Should().NotBeNull();
        var schema = await provider!.DiscoverAsync();
        schema.Tables.Select(t => t.Name).Should().Contain("tenant_widgets",
            "the session must introspect its own database, not the operator's");
    }

    [Fact]
    public async Task AProjectsExternalDatabaseWinsOverTheWorkspaceConnection()
    {
        if (_postgresConnectionString is null) return;

        var workspaceWide = await _catalog.AddConnectionAsync(_workspaceId, "workspace-wide", "sqlite", _editorId, $"Data Source={_sqliteFile}");
        var projectBound = await _catalog.AddConnectionAsync(_workspaceId, "project-bound", "postgres", _editorId, _postgresConnectionString);
        _ = workspaceWide;

        var projectId = Guid.NewGuid();
        await _projectDatabases.SaveAsync(new ProjectDatabase(
            Guid.NewGuid(), projectId, "warehouse", ProjectDatabaseType.External, "postgres", "active", projectBound.Id, DateTimeOffset.UtcNow));

        var provider = await _resolver.ResolveAsync(_workspaceId, projectId);

        provider.Should().NotBeNull();
        var schema = await provider!.DiscoverAsync();
        schema.Tables.Select(t => t.Name).Should().Contain("tenant_orders",
            "a project bound to an external database must introspect that one");
    }

    [Fact]
    public async Task AWorkspaceWithNoConnectionFallsBackToTheInstanceProvider()
    {
        (await _resolver.ResolveAsync(_workspaceId, projectId: null)).Should().BeNull(
            "null is how the resolver says 'use the configured default', which is what keeps the CLI and " +
            "single-tenant self-hosting working unchanged");
    }

    [Fact]
    public async Task SeveralConnectionsAndNoProjectBindingFallsBackRatherThanGuessing()
    {
        await _catalog.AddConnectionAsync(_workspaceId, "first", "sqlite", _editorId, $"Data Source={_sqliteFile}");
        await _catalog.AddConnectionAsync(_workspaceId, "second", "sqlite", _editorId, $"Data Source={_sqliteFile}?x=2");

        (await _resolver.ResolveAsync(_workspaceId, projectId: null)).Should().BeNull(
            "guessing which of a customer's databases to introspect is worse than using the configured default");
    }

    [Fact]
    public async Task ADisabledConnectionIsNotResolved()
    {
        var created = await _catalog.AddConnectionAsync(_workspaceId, "primary", "sqlite", _editorId, $"Data Source={_sqliteFile}");
        await _catalog.UpdateStatusAsync(created.Id, "disabled", _editorId, _workspaceId);

        (await _resolver.ResolveAsync(_workspaceId, projectId: null)).Should().BeNull();
    }

    [Fact]
    public async Task AConnectionWithNoStoredSecretIsNotResolved()
    {
        await _catalog.AddConnectionAsync(_workspaceId, "declared-only", "sqlite", _editorId, connectionString: null);

        (await _resolver.ResolveAsync(_workspaceId, projectId: null)).Should().BeNull(
            "a connection without a connection string has nothing to connect to");
    }

    [Fact]
    public async Task ValidationSucceedsAgainstARealPostgreSql()
    {
        if (_postgresConnectionString is null) return;

        var created = await _catalog.AddConnectionAsync(_workspaceId, "pg", "postgres", _editorId, _postgresConnectionString);

        var result = await _catalog.ValidateAsync(_workspaceId, created.Id, _editorId);

        result.IsReachable.Should().BeTrue(result.Message);
        (await _catalog.GetAsync(_workspaceId, created.Id, _editorId))!.LastValidatedAt.Should().NotBeNull();
    }
}
