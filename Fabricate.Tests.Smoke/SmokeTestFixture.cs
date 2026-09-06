namespace Fabricate.Tests.Smoke;

/// <summary>
/// Shared HTTP client fixture for smoke tests.
///
/// <para>
/// Tests self-skip when <c>SMOKE_API_BASE_URL</c> or <c>SMOKE_API_KEY</c> is absent, so the suite is harmless to
/// run locally. That convenience is also a hazard: a self-skipping test is recorded by the test runner as
/// <em>passed</em> and <em>executed</em>, so a deploy pipeline asserting on executed and passed counts is
/// satisfied by a run in which nothing was checked at all (#61).
/// </para>
///
/// <para>
/// <c>SMOKE_REQUIRE_EXECUTION=1</c> is what closes that. Set it wherever the suite is meant to verify a real
/// deployment, and the absent gates become a failure instead of a silent skip. It lives here rather than in the
/// workflow so the protection cannot be lost by editing YAML.
/// </para>
/// </summary>
public sealed class SmokeTestFixture : IDisposable
{
    public static readonly string? BaseUrl = Environment.GetEnvironmentVariable("SMOKE_API_BASE_URL")?.TrimEnd('/');
    public static readonly string? ApiKey = Environment.GetEnvironmentVariable("SMOKE_API_KEY");

    /// <summary>Whether the caller has declared that these tests must really run.</summary>
    public static bool ExecutionRequired =>
        Environment.GetEnvironmentVariable("SMOKE_REQUIRE_EXECUTION") == "1";

    private static bool Gated => string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>
    /// True when the test should return without asserting. Throws instead when execution was required, so the
    /// difference between "not configured" and "configured and broken" cannot be mistaken for success.
    /// </summary>
    public static bool ShouldSkip
    {
        get
        {
            if (!Gated) return false;

            if (ExecutionRequired)
            {
                var missing = new List<string>();
                if (string.IsNullOrWhiteSpace(BaseUrl)) missing.Add(nameof(SMOKE_API_BASE_URL));
                if (string.IsNullOrWhiteSpace(ApiKey)) missing.Add(nameof(SMOKE_API_KEY));

                throw new InvalidOperationException(
                    $"SMOKE_REQUIRE_EXECUTION=1 says these tests must verify a real deployment, but " +
                    $"{string.Join(" and ", missing)} {(missing.Count == 1 ? "is" : "are")} not set. " +
                    "Skipping here would report success for a deployment nothing checked.");
            }

            return true;
        }
    }

    // Named constants so the message above names the variable rather than repeating a string literal.
    private const string SMOKE_API_BASE_URL = nameof(SMOKE_API_BASE_URL);
    private const string SMOKE_API_KEY = nameof(SMOKE_API_KEY);

    public HttpClient Client { get; }

    public SmokeTestFixture()
    {
        Client = new HttpClient { BaseAddress = new Uri(BaseUrl ?? "http://localhost:8080") };
        if (!string.IsNullOrWhiteSpace(ApiKey))
            Client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
    }

    public void Dispose() => Client.Dispose();
}
