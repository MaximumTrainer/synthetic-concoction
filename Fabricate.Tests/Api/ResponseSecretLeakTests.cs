using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fabricate.Api;
using FluentAssertions;

namespace Fabricate.Tests.Api;

/// <summary>
/// #89: API-key reads returned the domain record whole, so every response carried <c>HashedSecret</c>. A hash is
/// not the key, but it is still credential material and it is exactly what an offline cracking attempt needs.
/// Webhook reads had the same shape of problem with <c>SigningSecret</c>, which is not hashed at all.
///
/// <para>
/// These walk real endpoints and fail on any response body carrying a secret-shaped property. The allowlist is
/// deliberately tiny and explicit: the two responses that legitimately disclose a secret once.
/// </para>
/// </summary>
[Collection("ApiIntegration")]
public sealed class ResponseSecretLeakTests
{
    private static readonly Guid Account = StartupBootstrapService.BootstrapAccountId;

    /// <summary>Property names that must never appear in a response body outside the allowlisted routes.</summary>
    private static readonly string[] Forbidden =
    [
        "hashedSecret", "signingSecret", "cipherText", "passwordHash", "plaintextSecret",
    ];

    private static FabricateApiFactory NewFactory()
        => new(new Dictionary<string, string?>(StringComparer.Ordinal) { ["FABRICATE_API_USAGE_SAMPLING"] = "0" });

    /// <summary>
    /// Fails if the body carries any forbidden property. <paramref name="allowed"/> names the one-time
    /// disclosures — a create response handing back the plaintext it just generated — and nothing else.
    /// </summary>
    private static void AssertNoSecrets(string route, string body, params string[] allowed)
    {
        foreach (var name in Forbidden.Except(allowed, StringComparer.OrdinalIgnoreCase))
        {
            body.Should().NotContainEquivalentOf($"\"{name}\"", $"{route} must not disclose {name}");
        }
    }

    [Fact]
    public async Task ApiKeyReadsDoNotDiscloseTheStoredHash()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var created = await client.PostAsJsonAsync(
            new Uri($"/accounts/{Account}/api-keys", UriKind.Relative),
            new { name = "ci", scopes = new[] { "read" }, expiry = (string?)null });
        created.StatusCode.Should().Be(HttpStatusCode.OK);

        var createdBody = await created.Content.ReadAsStringAsync();
        AssertNoSecrets("POST /accounts/{id}/api-keys", createdBody, allowed: "plaintextSecret");

        using var createdDocument = JsonDocument.Parse(createdBody);
        createdDocument.RootElement.GetProperty("plaintextSecret").GetString().Should().NotBeNullOrWhiteSpace(
            "creation is the one moment the plaintext is disclosed");
        var keyId = createdDocument.RootElement.GetProperty("id").GetGuid();

        using var listed = await client.GetAsync(new Uri($"/accounts/{Account}/api-keys", UriKind.Relative));
        var listedBody = await listed.Content.ReadAsStringAsync();
        AssertNoSecrets("GET /accounts/{id}/api-keys", listedBody);
        listedBody.Should().NotContain("Secret", "the plaintext is never returned again");
        listedBody.Should().Contain("\"name\":\"ci\"", "the projection must still carry what the caller needs");
        listedBody.Should().Contain("\"isActive\"");

        using var revoked = await client.DeleteAsync(new Uri($"/accounts/{Account}/api-keys/{keyId}", UriKind.Relative));
        var revokedBody = await revoked.Content.ReadAsStringAsync();
        AssertNoSecrets("DELETE /accounts/{id}/api-keys/{keyId}", revokedBody);
        revokedBody.Should().Contain("\"isRevoked\":true");
    }

    [Fact]
    public async Task WebhookReadsDoNotDiscloseTheSigningSecret()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var workspaceResponse = await client.PostAsJsonAsync(
            new Uri("/workspaces", UriKind.Relative), new { accountId = Account, name = "hooks" });
        using var workspaceDocument = JsonDocument.Parse(await workspaceResponse.Content.ReadAsStringAsync());
        var workspaceId = workspaceDocument.RootElement.GetProperty("id").GetGuid();

        const string secret = "whsec_LIVE_SIGNING_SECRET_VALUE";
        using var registered = await client.PostAsJsonAsync(
            new Uri($"/workspaces/{workspaceId}/webhooks", UriKind.Relative),
            new { url = "https://example.test/hook", events = new[] { "run.completed" }, signingSecret = secret });
        registered.StatusCode.Should().Be(HttpStatusCode.OK);

        using var registeredDocument = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());
        var webhookId = registeredDocument.RootElement.GetProperty("id").GetGuid();

        using var listed = await client.GetAsync(new Uri($"/workspaces/{workspaceId}/webhooks", UriKind.Relative));
        var listedBody = await listed.Content.ReadAsStringAsync();
        AssertNoSecrets("GET /workspaces/{id}/webhooks", listedBody);
        listedBody.Should().NotContain(secret);
        listedBody.Should().Contain("\"hasSigningSecret\":true",
            "callers still need to know whether a webhook is signed, just not what with");

        using var fetched = await client.GetAsync(new Uri($"/workspaces/{workspaceId}/webhooks/{webhookId}", UriKind.Relative));
        var fetchedBody = await fetched.Content.ReadAsStringAsync();
        AssertNoSecrets("GET /workspaces/{id}/webhooks/{webhookId}", fetchedBody);
        fetchedBody.Should().NotContain(secret);
    }

    [Fact]
    public async Task NoReadEndpointDisclosesASecretShapedProperty()
    {
        using var factory = NewFactory();
        using var client = factory.CreateAuthenticatedClient();

        using var workspaceResponse = await client.PostAsJsonAsync(
            new Uri("/workspaces", UriKind.Relative), new { accountId = Account, name = "sweep" });
        using var workspaceDocument = JsonDocument.Parse(await workspaceResponse.Content.ReadAsStringAsync());
        var workspaceId = workspaceDocument.RootElement.GetProperty("id").GetGuid();

        // Every GET reachable without further setup. A read is where a leak lasts: it is the response an operator
        // pipes into a log, a dashboard or a support ticket.
        string[] routes =
        [
            $"/accounts/{Account}",
            $"/accounts/{Account}/members",
            $"/accounts/{Account}/api-keys",
            $"/accounts/{Account}/audit?pageSize=20",
            $"/accounts/{Account}/llm-usage",
            $"/workspaces/{workspaceId}",
            $"/workspaces/{workspaceId}/projects",
            $"/workspaces/{workspaceId}/webhooks",
            $"/workspaces/{workspaceId}/connections",
            $"/workspaces/{workspaceId}/llm-credentials",
            $"/workspaces/{workspaceId}/llm-credentials/policy",
            $"/workspaces/{workspaceId}/llm-usage",
            "/runs?page=1&pageSize=20",
        ];

        foreach (var route in routes)
        {
            using var response = await client.GetAsync(new Uri(route, UriKind.Relative));
            response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError, $"{route} must not fault");

            AssertNoSecrets(route, await response.Content.ReadAsStringAsync());
        }
    }
}
