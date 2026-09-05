using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Api;

/// <summary>
/// How much authenticated request traffic is written to the audit log (#72).
/// </summary>
public sealed record ApiUsageAuditOptions
{
    /// <summary>
    /// Fraction of authenticated requests recorded, 0.0–1.0. Defaults to 1.0: a deployment small enough not to
    /// have tuned this is small enough to record everything, and a half-populated usage log is worse than none
    /// for answering "what did this key do".
    /// </summary>
    public double SamplingRate { get; init; } = 1.0;

    public bool IsEnabled => SamplingRate > 0;

    public static ApiUsageAuditOptions FromEnvironment(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (!double.TryParse(read("FABRICATE_API_USAGE_SAMPLING"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
        {
            return new ApiUsageAuditOptions();
        }

        return new ApiUsageAuditOptions { SamplingRate = Math.Clamp(rate, 0.0, 1.0) };
    }
}

/// <summary>
/// Records one <c>api.request</c> audit event per authenticated request: which key called which route template,
/// with what outcome and how long it took (#72). Nothing else — no headers, no query values, no bodies.
///
/// <para>
/// The <em>route template</em> is recorded rather than the request path. A path carries workspace, project and
/// session identifiers; the template (<c>/workspaces/{workspaceId}/projects/{projectId}</c>) says which endpoint
/// was called without copying tenant identifiers into a log that is exported and kept for months. Requests that
/// match no endpoint record no template at all, since an unmatched path is entirely caller-controlled.
/// </para>
/// </summary>
public sealed class ApiUsageAuditMiddleware(
    RequestDelegate next,
    ApiUsageAuditOptions options,
    ILogger<ApiUsageAuditMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IAuditLogService auditLog)
    {
        if (!options.IsEnabled)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var timestamp = Stopwatch.GetTimestamp();
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            // In a finally block so a failing request is recorded too — those are the ones worth having.
            await TryRecordAsync(context, auditLog, Stopwatch.GetElapsedTime(timestamp)).ConfigureAwait(false);
        }
    }

    private async Task TryRecordAsync(HttpContext context, IAuditLogService auditLog, TimeSpan elapsed)
    {
        try
        {
            // Anonymous endpoints (/healthz, Swagger) have no key to attribute usage to and are not recorded.
            if (context.User.Identity?.IsAuthenticated != true) return;
            if (!Guid.TryParse(context.User.FindFirst("account_id")?.Value, out var accountId)) return;
            if (!ShouldSample()) return;

            Guid? apiKeyId = Guid.TryParse(context.User.FindFirst("api_key_id")?.Value, out var keyId) ? keyId : null;
            var scopes = string.Join('|', context.User.FindAll("scope").Select(c => c.Value));
            var routeTemplate = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;

            var details =
                $"method={context.Request.Method};" +
                $"route={routeTemplate ?? "(unmatched)"};" +
                $"status={context.Response.StatusCode};" +
                $"scopes={scopes};" +
                $"durationMs={elapsed.TotalMilliseconds:F0}";

            await auditLog.RecordAsync(
                new AuditEvent(
                    Guid.NewGuid(),
                    accountId,
                    accountId,
                    "api.request",
                    "Endpoint",
                    routeTemplate,
                    context.TraceIdentifier,
                    DateTimeOffset.UtcNow,
                    details,
                    apiKeyId),
                // Not the request's own token: a cancelled or aborted request is exactly the kind whose usage
                // record matters, and passing the aborted token would drop it.
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Usage accounting must never turn a served request into a failed one.
            logger.LogError(ex, "Failed to record API usage audit event.");
        }
    }

    private bool ShouldSample()
        => options.SamplingRate >= 1.0 || RandomNumberGenerator.GetInt32(0, 10_000) < options.SamplingRate * 10_000;
}
