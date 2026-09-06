using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Fabricate.Tests.Api;

/// <summary>
/// #79: authentication and authorisation asserted through HTTP against the composed application, which is where
/// these properties actually live. Nothing below reaches into a service directly.
/// </summary>
[Collection("ApiIntegration")]
public sealed class ApiAuthenticationTests : IDisposable
{
    private static readonly Guid BootstrapAccountId = new("00000000-0000-0000-0000-000000000001");

    private readonly FabricateApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    /// <summary>Every authenticated route group, one representative route each.</summary>
    public static TheoryData<string, string> ProtectedRoutes => new()
    {
        { "GET", $"/accounts/{BootstrapAccountId}" },
        { "GET", $"/accounts/{BootstrapAccountId}/api-keys" },
        { "GET", $"/workspaces/{Guid.Empty}" },
        { "GET", $"/workspaces/{Guid.Empty}/projects" },
        // The workflows group exposes no list route, so use one of its GETs.
        { "GET", $"/workspaces/{Guid.Empty}/workflows/{Guid.Empty}/runs/{Guid.Empty}" },
        { "GET", $"/workspaces/{Guid.Empty}/webhooks" },
        { "GET", $"/workspaces/{Guid.Empty}/llm-credentials" },
        { "GET", $"/workspaces/{Guid.Empty}/chat/sessions/{Guid.Empty}/messages" },
        // Runs moved under the workspace with #66; there is no instance-wide route any more.
        { "GET", $"/workspaces/{Guid.Empty}/runs" },
        { "GET", $"/workspaces/{Guid.Empty}/runs/{Guid.Empty}/artifacts" },
        { "GET", $"/workspaces/{Guid.Empty}/schema-snapshots" },
        { "GET", $"/workspaces/{Guid.Empty}/llm-usage" },
        { "GET", $"/accounts/{BootstrapAccountId}/audit" },
    };

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task ProtectedRoutes_RejectAMissingKey(string method, string route)
    {
        using var client = _factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), route));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{method} {route} must require authentication");
    }

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task ProtectedRoutes_RejectAnUnknownKey(string method, string route)
    {
        using var client = _factory.CreateAuthenticatedClient("cnc_not-a-real-key");

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), route));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bearer something")]
    [InlineData("cnc_")]
    public async Task MalformedKeys_AreRejectedConsistently(string apiKey)
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

        var response = await client.GetAsync($"/accounts/{BootstrapAccountId}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheBootstrapKey_Authenticates()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync($"/accounts/{BootstrapAccountId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RevokedKeys_AreRejected()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var created = await client.PostAsJsonAsync($"/accounts/{BootstrapAccountId}/api-keys",
            new { name = "temp", scopes = new[] { "read" }, expiry = (TimeSpan?)null });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var key = await created.Content.ReadFromJsonAsync<CreatedKey>();
        key!.PlaintextSecret.Should().NotBeNullOrWhiteSpace();

        using (var issued = _factory.CreateAuthenticatedClient(key.PlaintextSecret))
        {
            (await issued.GetAsync($"/accounts/{BootstrapAccountId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var revoked = await client.DeleteAsync($"/accounts/{BootstrapAccountId}/api-keys/{key.Id}");
        revoked.StatusCode.Should().Be(HttpStatusCode.OK);

        using var afterRevoke = _factory.CreateAuthenticatedClient(key.PlaintextSecret);
        (await afterRevoke.GetAsync($"/accounts/{BootstrapAccountId}")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "a revoked key must stop working immediately");
    }

    [Fact]
    public async Task ExpiredKeys_AreRejected()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var created = await client.PostAsJsonAsync($"/accounts/{BootstrapAccountId}/api-keys",
            new { name = "expired", scopes = new[] { "read" }, expiry = TimeSpan.FromMilliseconds(1) });
        var key = await created.Content.ReadFromJsonAsync<CreatedKey>();

        await Task.Delay(50);

        using var expired = _factory.CreateAuthenticatedClient(key!.PlaintextSecret);
        (await expired.GetAsync($"/accounts/{BootstrapAccountId}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task HealthAndDocs_StayAnonymous()
    {
        using var client = _factory.CreateClient();

        (await client.GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_ReportsLlmStateWithoutLeakingAnyValue()
    {
        using var client = _factory.CreateClient();

        var body = await client.GetStringAsync("/healthz");

        body.Should().Contain("healthy").And.Contain("platformCredential");
        body.Should().NotContain("sk-").And.NotContain(FabricateApiFactory.BootstrapApiKey);
    }

    [Fact]
    public async Task Health_ReportsTheDatabaseWhenItIsReachable()
    {
        using var client = _factory.CreateClient();

        var body = await client.GetStringAsync("/healthz");

        // The default factory runs the in-memory repositories, which is a legitimate configuration rather than a
        // fault: there is no database to be unreachable.
        body.Should().Contain("database").And.Contain("not configured");
        body.Should().Contain("healthy");
    }

    private sealed record CreatedKey(Guid Id, string Name, string PlaintextSecret);
}
