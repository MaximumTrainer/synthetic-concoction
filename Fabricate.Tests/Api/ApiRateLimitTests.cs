using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Fabricate.Tests.Api;

/// <summary>
/// #68: the `api` policy was defined but attached to no endpoint, so the advertised 100 req/min was not enforced
/// anywhere. These tests assert it is applied, is partitioned per key, and answers with 429 + Retry-After.
/// </summary>
[Collection("ApiIntegration")]
public sealed class ApiRateLimitTests : IDisposable
{
    private static readonly Guid BootstrapAccountId = new("00000000-0000-0000-0000-000000000001");
    private const int Limit = 5;

    private readonly FabricateApiFactory _factory = new(new Dictionary<string, string?>
    {
        ["FABRICATE_API_RATE_LIMIT_PER_MINUTE"] = Limit.ToString(),
    });

    public void Dispose() => _factory.Dispose();

    private async Task<HttpResponseMessage> CallAsync(HttpClient client)
        => await client.GetAsync($"/accounts/{BootstrapAccountId}");

    [Fact]
    public async Task RequestsBeyondTheLimit_Get429WithRetryAfter()
    {
        using var client = _factory.CreateAuthenticatedClient();

        for (var i = 1; i <= Limit; i++)
        {
            (await CallAsync(client)).StatusCode.Should().Be(HttpStatusCode.OK, $"request {i} is within the limit");
        }

        var rejected = await CallAsync(client);

        rejected.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.RetryAfter.Should().NotBeNull("clients need to know when to retry");
        (await rejected.Content.ReadAsStringAsync()).Should().Contain("Too many requests");
    }

    [Fact]
    public async Task TheWindowIsPerKey_SoOneTenantCannotExhaustAnother()
    {
        using var first = _factory.CreateAuthenticatedClient();

        // Issue a second key and spend the first key's whole window.
        var created = await first.PostAsJsonAsync($"/accounts/{BootstrapAccountId}/api-keys",
            new { name = "second", scopes = new[] { "read" }, expiry = (TimeSpan?)null });
        var second = await created.Content.ReadFromJsonAsync<CreatedKey>();

        for (var i = 0; i < Limit; i++) await CallAsync(first);
        (await CallAsync(first)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        using var other = _factory.CreateAuthenticatedClient(second!.PlaintextSecret);
        (await CallAsync(other)).StatusCode.Should().Be(HttpStatusCode.OK, "a second key has its own window");
    }

    [Fact]
    public async Task HealthIsNotRateLimited()
    {
        using var client = _factory.CreateClient();

        for (var i = 0; i < Limit * 3; i++)
        {
            (await client.GetAsync("/healthz")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    private sealed record CreatedKey(Guid Id, string Name, string PlaintextSecret);
}
