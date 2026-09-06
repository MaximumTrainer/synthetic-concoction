using System.Net;
using Fabricate.Tests.Api;
using FluentAssertions;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace Fabricate.Tests.Integration;

/// <summary>
/// #61: on a hosted platform the health check decides whether an instance receives traffic. The database is the
/// one dependency whose absence makes the instance unserviceable — every authenticated route reads through it —
/// so a machine that cannot reach it must report unhealthy and be taken out of rotation, rather than stay in and
/// answer 500 to everything.
///
/// <para>
/// This runs against a real PostgreSQL and then <em>stops it</em>, because the failure worth testing is not a
/// database that was never there — an instance whose database is missing at boot fails to start, and the platform
/// restarts it — but one that was reachable and went away while the process kept running. Only the second is
/// invisible without a readiness signal.
/// </para>
/// </summary>
[Collection("ApiIntegration")]
public sealed class HealthReadinessTests(ITestOutputHelper output) : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private string? _connectionString;
    private string? _failure;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("FABRICATE_SKIP_DOCKER_TESTS") == "1") return;

        try
        {
            _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await _postgres.StartAsync();
            _connectionString = _postgres.GetConnectionString();
        }
        catch (Exception ex)
        {
            _failure = ex.ToString();
            _connectionString = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [Fact]
    public void ThePostgresContainerStartedWhenDockerIsAvailable()
    {
        output.WriteLine(_connectionString is not null
            ? "Health readiness (#61): EXERCISED against PostgreSQL."
            : _failure is not null
                ? $"Health readiness (#61): FAILED — {_failure.Split('\n')[0].Trim()}"
                : "Health readiness (#61): not run (no Docker).");

        if (_failure is null) return;

        _failure.Should().Contain("DockerUnavailableException",
            "any failure other than a missing Docker daemon is ours, and must not pass as a skip");
    }

    [Fact]
    public async Task HealthGoesUnhealthyWhenTheDatabaseGoesAway()
    {
        if (_connectionString is null) return;

        using var factory = new FabricateApiFactory(new Dictionary<string, string?>
        {
            ["FABRICATE_DB_PROVIDER"] = "postgres",
            // A short timeout so the probe answers within the health check's own budget rather than hanging.
            ["FABRICATE_CONNECTION_STRING"] = _connectionString + ";Timeout=3;Command Timeout=3",
        });

        using var client = factory.CreateClient();

        using (var healthy = await client.GetAsync("/healthz"))
        {
            healthy.StatusCode.Should().Be(HttpStatusCode.OK);
            (await healthy.Content.ReadAsStringAsync()).Should().Contain("reachable");
        }

        // The database goes away underneath a running process — a failover, a plan change, a network partition.
        await _postgres!.StopAsync();

        using var unhealthy = await client.GetAsync("/healthz");

        unhealthy.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "a machine that cannot reach its database should stop receiving traffic");

        var body = await unhealthy.Content.ReadAsStringAsync();
        body.Should().Contain("unreachable");

        // The probe says what is wrong, never how to connect to it.
        body.Should().NotContain("Password").And.NotContain("Username").And.NotContain("Host=");
    }
}
