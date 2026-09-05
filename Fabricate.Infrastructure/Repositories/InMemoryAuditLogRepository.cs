using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Repositories;

public sealed class InMemoryAuditLogRepository : IAuditLogRepository
{
    private readonly List<AuditEvent> _events = [];

    /// <summary>Every event appended, in insertion order. Intended for tests.</summary>
    public IReadOnlyList<AuditEvent> All => _events;

    public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        _events.Add(auditEvent);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEvent>> QueryAsync(Guid accountId, int skip, int take, string? actionFilter, CancellationToken cancellationToken = default)
    {
        var query = _events.Where(e => e.AccountId == accountId);
        if (actionFilter is not null)
        {
            query = query.Where(e => e.Action.Contains(actionFilter, StringComparison.OrdinalIgnoreCase));
        }

        var result = query.OrderByDescending(e => e.OccurredAt).Skip(skip).Take(take).ToArray();
        return Task.FromResult<IReadOnlyList<AuditEvent>>(result);
    }

    public Task<int> CountAsync(Guid accountId, string? actionFilter, CancellationToken cancellationToken = default)
    {
        var query = _events.Where(e => e.AccountId == accountId);
        if (actionFilter is not null)
        {
            query = query.Where(e => e.Action.Contains(actionFilter, StringComparison.OrdinalIgnoreCase));
        }

        return Task.FromResult(query.Count());
    }

    public async IAsyncEnumerable<AuditEvent> StreamAsync(
        Guid accountId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Snapshot before yielding: an append during the enumeration must not disturb it.
        var window = _events
            .Where(e => e.AccountId == accountId)
            .Where(e => (from is null || e.OccurredAt >= from) && (to is null || e.OccurredAt <= to))
            .OrderBy(e => e.OccurredAt)
            .ToArray();

        foreach (var auditEvent in window)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return auditEvent;
            await Task.Yield();
        }
    }

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken = default)
    {
        var doomed = _events.Where(e => e.OccurredAt < cutoff).Take(batchSize).ToArray();
        foreach (var auditEvent in doomed)
        {
            _events.Remove(auditEvent);
        }

        return Task.FromResult(doomed.Length);
    }
}
