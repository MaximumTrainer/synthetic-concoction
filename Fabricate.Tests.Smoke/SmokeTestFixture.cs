namespace Fabricate.Tests.Smoke;

/// <summary>
/// Shared HTTP client fixture for smoke tests.
/// Tests are skipped automatically when <c>SMOKE_API_BASE_URL</c> is not set.
/// </summary>
public sealed class SmokeTestFixture : IDisposable
{
    public static readonly string? BaseUrl = Environment.GetEnvironmentVariable("SMOKE_API_BASE_URL")?.TrimEnd('/');
    public static readonly string? ApiKey = Environment.GetEnvironmentVariable("SMOKE_API_KEY");

    public static bool ShouldSkip => string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(ApiKey);

    public HttpClient Client { get; }

    public SmokeTestFixture()
    {
        Client = new HttpClient { BaseAddress = new Uri(BaseUrl ?? "http://localhost:8080") };
        if (!string.IsNullOrWhiteSpace(ApiKey))
            Client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
    }

    public void Dispose() => Client.Dispose();
}
