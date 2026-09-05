using System.Globalization;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Repositories;

public sealed class InMemoryLlmUsageRepository : ILlmUsageRepository
{
    private readonly List<LlmUsageRecord> _records = [];
    private readonly object _lock = new();

    /// <summary>Every record written, in order. Intended for tests.</summary>
    public IReadOnlyList<LlmUsageRecord> All
    {
        get { lock (_lock) return _records.ToArray(); }
    }

    public Task RecordAsync(LlmUsageRecord record, CancellationToken cancellationToken = default)
    {
        lock (_lock) _records.Add(record);
        return Task.CompletedTask;
    }

    public Task<LlmUsageSummary> SummariseWorkspaceAsync(Guid workspaceId, DateTimeOffset from, DateTimeOffset to, LlmUsageGrouping groupBy, CancellationToken cancellationToken = default)
        => SummariseWorkspacesAsync([workspaceId], from, to, groupBy, cancellationToken);

    public Task<LlmUsageSummary> SummariseWorkspacesAsync(IReadOnlyCollection<Guid> workspaceIds, DateTimeOffset from, DateTimeOffset to, LlmUsageGrouping groupBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceIds);
        var window = Window(workspaceIds, from, to);
        return Task.FromResult(LlmUsageRollup.Build(window, from, to, groupBy));
    }

    public Task<long> TotalTokensAsync(Guid workspaceId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        => Task.FromResult(Window([workspaceId], from, to).Sum(r => r.InputTokens + r.OutputTokens));

    private LlmUsageRecord[] Window(IReadOnlyCollection<Guid> workspaceIds, DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            return _records
                .Where(r => workspaceIds.Contains(r.WorkspaceId) && r.OccurredAt >= from && r.OccurredAt < to)
                .ToArray();
        }
    }
}

/// <summary>
/// The grouping and totalling both adapters share, so an in-memory query and a database one cannot disagree about
/// what a bucket means.
/// </summary>
internal static class LlmUsageRollup
{
    internal static LlmUsageSummary Build(
        IReadOnlyCollection<LlmUsageRecord> records,
        DateTimeOffset from,
        DateTimeOffset to,
        LlmUsageGrouping groupBy)
    {
        var buckets = records
            .GroupBy(r => KeyFor(r, groupBy), StringComparer.Ordinal)
            .Select(g => new LlmUsageBucket(
                g.Key,
                g.Sum(r => r.InputTokens),
                g.Sum(r => r.OutputTokens),
                g.Count(),
                g.Count(r => r.Outcome != LlmCallOutcome.Success)))
            .OrderByDescending(b => b.TotalTokens)
            .ThenBy(b => b.Key, StringComparer.Ordinal)
            .ToArray();

        return new LlmUsageSummary(
            from,
            to,
            groupBy,
            records.Sum(r => r.InputTokens),
            records.Sum(r => r.OutputTokens),
            records.Count,
            records.Count(r => r.Outcome != LlmCallOutcome.Success),
            buckets);
    }

    internal static string KeyFor(LlmUsageRecord record, LlmUsageGrouping groupBy) => groupBy switch
    {
        LlmUsageGrouping.Credential => record.CredentialId?.ToString() ?? "platform",
        LlmUsageGrouping.Day => record.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => record.Model,
    };
}
