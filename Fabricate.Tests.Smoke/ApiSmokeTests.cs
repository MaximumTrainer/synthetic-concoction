using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Fabricate.Tests.Smoke;

/// <summary>
/// Post-deployment HTTP smoke tests.
/// All tests skip gracefully when <c>SMOKE_API_BASE_URL</c> or <c>SMOKE_API_KEY</c> is not set.
/// </summary>
public sealed class ApiSmokeTests : IClassFixture<SmokeTestFixture>
{
    // The bootstrap service pre-seeds this account so we can use it without a prior setup step.
    private static readonly Guid BootstrapAccountId = new("00000000-0000-0000-0000-000000000001");

    private readonly SmokeTestFixture _fixture;

    public ApiSmokeTests(SmokeTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task HealthEndpoint_ShouldReturn200()
    {
        if (SmokeTestFixture.ShouldSkip)
            return;

        using var client = new HttpClient { BaseAddress = _fixture.Client.BaseAddress };
        var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("healthy");
    }

    [Fact]
    public async Task OpenApiSpec_ShouldReturn200()
    {
        if (SmokeTestFixture.ShouldSkip)
            return;

        var response = await _fixture.Client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetAccount_ShouldReturn200_ForBootstrapAccount()
    {
        if (SmokeTestFixture.ShouldSkip)
            return;

        var response = await _fixture.Client.GetAsync($"/accounts/{BootstrapAccountId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(BootstrapAccountId.ToString());
    }

    [Fact]
    public async Task CreateWorkspace_ShouldReturn200()
    {
        if (SmokeTestFixture.ShouldSkip)
            return;

        var payload = new { name = $"smoke-ws-{Guid.NewGuid():N}"[..20] };
        var response = await _fixture.Client.PostAsJsonAsync(
            $"/accounts/{BootstrapAccountId}/workspaces", payload);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
    }

    [Fact]
    public async Task ListWorkspaces_ShouldReturn200()
    {
        if (SmokeTestFixture.ShouldSkip)
            return;

        var response = await _fixture.Client.GetAsync(
            $"/accounts/{BootstrapAccountId}/workspaces");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UnauthenticatedRequest_ShouldReturn401()
    {
        if (SmokeTestFixture.ShouldSkip)
            return;

        using var unauthClient = new HttpClient { BaseAddress = _fixture.Client.BaseAddress };
        var response = await unauthClient.GetAsync($"/accounts/{BootstrapAccountId}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
