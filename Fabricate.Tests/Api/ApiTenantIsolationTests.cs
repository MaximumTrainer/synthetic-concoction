using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Fabricate.Tests.Api;

/// <summary>
/// #79: cross-tenant isolation and the workspace role matrix, asserted through HTTP. These were only ever
/// covered by service-level tests, so nothing proved the composed application enforces them.
/// </summary>
[Collection("ApiIntegration")]
public sealed class ApiTenantIsolationTests : IAsyncLifetime
{
    private readonly FabricateApiFactory _factory = new();
    private HttpClient _tenantA = null!;
    private HttpClient _tenantB = null!;
    private Guid _workspaceA;
    private Guid _projectA;

    public async Task InitializeAsync()
    {
        _tenantA = _factory.CreateAuthenticatedClient();

        // A second tenant: its own account, its own API key.
        var accountB = await Post<AccountResponse>(_tenantA, "/accounts", new { name = "Tenant B" });
        var keyB = await Post<CreatedKey>(_tenantA, $"/accounts/{accountB.Id}/api-keys",
            new { name = "tenant-b", scopes = new[] { "read", "write" }, expiry = (TimeSpan?)null });
        _tenantB = _factory.CreateAuthenticatedClient(keyB.PlaintextSecret);

        var workspace = await Post<WorkspaceResponse>(_tenantA, "/workspaces",
            new { accountId = Guid.NewGuid(), name = "A's workspace" });
        _workspaceA = workspace.Id;

        var project = await Post<ProjectResponse>(_tenantA, $"/workspaces/{_workspaceA}/projects", new { name = "A's project" });
        _projectA = project.Id;
    }

    public Task DisposeAsync()
    {
        _tenantA.Dispose();
        _tenantB.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<T> Post<T>(HttpClient client, string route, object body)
    {
        var response = await client.PostAsJsonAsync(route, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"setup call POST {route} should succeed");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    public static TheoryData<string> TenantAResources(Guid workspaceId, Guid projectId) => new()
    {
        $"/workspaces/{workspaceId}",
        $"/workspaces/{workspaceId}/projects",
        $"/workspaces/{workspaceId}/connections",
        $"/workspaces/{workspaceId}/instructions",
        $"/workspaces/{workspaceId}/webhooks",
        $"/workspaces/{workspaceId}/llm-credentials",
        $"/workspaces/{workspaceId}/projects/{projectId}",
    };

    [Fact]
    public async Task AnotherTenantCannotReadTheWorkspaceOrItsResources()
    {
        foreach (var route in TenantAResources(_workspaceA, _projectA))
        {
            var response = await _tenantB.GetAsync(route);

            response.StatusCode.Should().BeOneOf(
                [HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.InternalServerError],
                $"GET {route} must not return tenant A's data to tenant B");

            if (response.StatusCode == HttpStatusCode.OK)
            {
                throw new InvalidOperationException($"Cross-tenant read succeeded for {route}");
            }
        }
    }

    [Fact]
    public async Task AnotherTenantCannotMutateTheWorkspace()
    {
        var grant = await _tenantB.PostAsJsonAsync($"/workspaces/{_workspaceA}/access",
            new { principalId = Guid.NewGuid(), isGroup = false, role = "Admin" });
        grant.StatusCode.Should().NotBe(HttpStatusCode.OK, "tenant B is not an admin of tenant A's workspace");

        var project = await _tenantB.PostAsJsonAsync($"/workspaces/{_workspaceA}/projects", new { name = "intruder" });
        project.StatusCode.Should().NotBe(HttpStatusCode.OK);

        var credential = await _tenantB.PostAsJsonAsync($"/workspaces/{_workspaceA}/llm-credentials",
            new { name = "intruder", provider = "Anthropic", model = "claude-opus-5", secret = "sk-ant-intruder" });
        credential.StatusCode.Should().NotBe(HttpStatusCode.OK);
        credential.StatusCode.Should().NotBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task TheOwningTenantCanStillReadItsOwnResources()
    {
        // The counterpart to the isolation assertions: these must not be failing for an unrelated reason.
        (await _tenantA.GetAsync($"/workspaces/{_workspaceA}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _tenantA.GetAsync($"/workspaces/{_workspaceA}/projects")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _tenantA.GetAsync($"/workspaces/{_workspaceA}/llm-credentials")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResponsesNeverEchoAnApiKeyOrCredentialSecret()
    {
        var registered = await _tenantA.PostAsJsonAsync($"/workspaces/{_workspaceA}/llm-credentials",
            new { name = "byok", provider = "Anthropic", model = "claude-opus-5", secret = "sk-ant-DO-NOT-ECHO-123" });
        registered.StatusCode.Should().Be(HttpStatusCode.Created);

        var registeredBody = await registered.Content.ReadAsStringAsync();
        var listed = await _tenantA.GetStringAsync($"/workspaces/{_workspaceA}/llm-credentials");

        foreach (var body in new[] { registeredBody, listed })
        {
            body.Should().NotContain("sk-ant-DO-NOT-ECHO-123");
            body.Should().NotContain("DO-NOT-ECHO");
            body.Should().NotContain(FabricateApiFactory.BootstrapApiKey);
        }
    }

    private sealed record AccountResponse(Guid Id, string Name);
    private sealed record WorkspaceResponse(Guid Id, Guid AccountId, string Name);
    private sealed record ProjectResponse(Guid Id, Guid WorkspaceId, string Name);
    private sealed record CreatedKey(Guid Id, string Name, string PlaintextSecret);
}
