namespace Fabricate.Domain.Models;

/// <summary>How a provider call ended. Distinguishes the cases that cost tokens from the ones that did not.</summary>
public enum LlmCallOutcome
{
    /// <summary>The provider answered. Token counts are the provider's own.</summary>
    Success = 0,

    /// <summary>The attempt failed and was retried. Recorded so a retry storm is visible rather than invisible.</summary>
    RetriedFailure,

    /// <summary>The attempt failed and was not retried — the call is over.</summary>
    Failure,
}

/// <summary>
/// One provider call attempt (#77). Every chat turn records token usage in its result and every call is logged,
/// but nothing aggregated it, so "what is this workspace spending" had no answer.
/// </summary>
/// <param name="AttemptNumber">
/// 1 for the first try. A retried call writes one record per attempt: a workspace whose calls keep failing and
/// retrying is burning latency and sometimes tokens, and a single record per turn would hide that.
/// </param>
public sealed record LlmUsageRecord(
    Guid Id,
    Guid WorkspaceId,
    Guid? ProjectId,
    Guid? SessionId,
    Guid? CredentialId,
    string Provider,
    string Model,
    long InputTokens,
    long OutputTokens,
    int AttemptNumber,
    long LatencyMs,
    LlmCallOutcome Outcome,
    DateTimeOffset OccurredAt)
{
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>How a usage query groups its rows.</summary>
public enum LlmUsageGrouping
{
    Model = 0,
    Credential,
    Day,
}

/// <summary>
/// One row of a usage rollup. <paramref name="Key"/> is the model name, credential id or UTC date depending on
/// the grouping asked for.
/// </summary>
public sealed record LlmUsageBucket(
    string Key,
    long InputTokens,
    long OutputTokens,
    long Calls,
    long FailedCalls)
{
    public long TotalTokens => InputTokens + OutputTokens;
}

public sealed record LlmUsageSummary(
    DateTimeOffset From,
    DateTimeOffset To,
    LlmUsageGrouping GroupBy,
    long InputTokens,
    long OutputTokens,
    long Calls,
    long FailedCalls,
    IReadOnlyList<LlmUsageBucket> Buckets)
{
    public long TotalTokens => InputTokens + OutputTokens;
}
