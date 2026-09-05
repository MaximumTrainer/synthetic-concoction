using System.Security.Cryptography;
using System.Text;

namespace Fabricate.Api;

/// <summary>
/// Rate-limit policy names and partitioning (#68). Every authenticated route group attaches
/// <see cref="Api"/>; without an explicit <c>RequireRateLimiting</c> a named policy is not enforced at all.
/// </summary>
public static class RateLimitPolicies
{
    public const string Api = "api";

    /// <summary>
    /// One window per API key so a noisy tenant cannot consume another's allowance. The key is hashed because
    /// the partition key ends up in limiter state and diagnostics, and the raw secret must not.
    /// Unauthenticated callers share a per-remote-address partition.
    /// </summary>
    public static string PartitionKey(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Api-Key", out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey.ToString()));
            return "key:" + Convert.ToHexStringLower(hash)[..16];
        }

        return "anon:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }
}
