using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fabricate.Api;
using FluentAssertions;

namespace Fabricate.Tests.Api;

/// <summary>
/// #83: the opt-in must be <em>refused</em> on a Healthcare or Finance workspace, not quietly ignored — an
/// administrator told "saved" while the setting did not take is worse off than one told why it cannot be.
/// </summary>
[Collection("ApiIntegration")]
public sealed class PromptDataBoundaryApiTests
{
    private static readonly Guid Account = StartupBootstrapService.BootstrapAccountId;

    private static FabricateApiFactory NewFactory()
        => new(new Dictionary<string, string?>(StringComparer.Ordinal) { ["FABRICATE_API_USAGE_SAMPLING"] = "0" });

    private static async Task<Guid> CreateWorkspaceAsync(HttpClient client, string complianceProfile)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/workspaces", UriKind.Relative),
            new { accountId = Account, name = $"ws-{complianceProfile}", complianceProfile });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        document.RootElement.GetProperty("complianceProfile").GetString().Should().Be(complianceProfile,
            "the profile is fixed at creation and must round-trip");
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> SetPolicyAsync(HttpClient client, Guid workspaceId, bool allowSampledData)
        => client.PutAsJsonAsync(
            new Uri($"/workspaces/{workspaceId}/llm-credentials/policy", UriKind.Relative),
            new { allowPlatformFallback = true, allowSampledDataInPrompts = allowSampledData });

    [Theory]
    [InlineData("Healthcare")]
    [InlineData("Finance")]
    public async Task TheOptInIsRefusedOnARegulatedWorkspace_AndThePolicyIsUnchanged(string profile)
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = await CreateWorkspaceAsync(client, profile);

        using var refused = await SetPolicyAsync(client, workspaceId, allowSampledData: true);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "the request is well-formed; it conflicts with the workspace's own compliance profile");
        (await refused.Content.ReadAsStringAsync()).Should().Contain(profile,
            "the error has to say why, or the administrator cannot act on it");

        using var read = await client.GetAsync(new Uri($"/workspaces/{workspaceId}/llm-credentials/policy", UriKind.Relative));
        using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("allowSampledDataInPrompts").GetBoolean().Should().BeFalse(
            "a refused opt-in must leave the policy exactly as it was");
    }

    [Fact]
    public async Task TheOptInIsAcceptedOnADefaultWorkspace()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = await CreateWorkspaceAsync(client, "Default");

        using var response = await SetPolicyAsync(client, workspaceId, allowSampledData: true);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var read = await client.GetAsync(new Uri($"/workspaces/{workspaceId}/llm-credentials/policy", UriKind.Relative));
        using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("allowSampledDataInPrompts").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task TurningTheOptInOffIsAlwaysAllowed()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = await CreateWorkspaceAsync(client, "Healthcare");

        using var response = await SetPolicyAsync(client, workspaceId, allowSampledData: false);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "only enabling is refused; a regulated workspace must still be able to set its other policy fields");
    }

    [Fact]
    public async Task ARefusedOptInIsAudited()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();
        var workspaceId = await CreateWorkspaceAsync(client, "Finance");

        using var refused = await SetPolicyAsync(client, workspaceId, allowSampledData: true);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var audit = await client.GetAsync(
            new Uri($"/accounts/{Account}/audit?action=boundary_blocked&pageSize=50", UriKind.Relative));
        var body = await audit.Content.ReadAsStringAsync();

        body.Should().Contain("llm.boundary_blocked");
        body.Should().Contain("reason=opt_in_refused");
        body.Should().Contain("complianceProfile=Finance");
    }

    [Fact]
    public async Task ANewWorkspaceDefaultsToTheDefaultProfileAndNoOptIn()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var created = await client.PostAsJsonAsync(
            new Uri("/workspaces", UriKind.Relative), new { accountId = Account, name = "plain" });
        using var document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());

        document.RootElement.GetProperty("complianceProfile").GetString().Should().Be("Default");

        var workspaceId = document.RootElement.GetProperty("id").GetGuid();
        using var read = await client.GetAsync(new Uri($"/workspaces/{workspaceId}/llm-credentials/policy", UriKind.Relative));
        using var policy = JsonDocument.Parse(await read.Content.ReadAsStringAsync());

        policy.RootElement.GetProperty("allowSampledDataInPrompts").GetBoolean().Should().BeFalse(
            "the boundary is closed until someone with authority opens it");
    }
}
