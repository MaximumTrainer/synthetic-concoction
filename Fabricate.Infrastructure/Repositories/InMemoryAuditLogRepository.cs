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

    public Task<IReadOnlyList<AuditEvent>> QueryAsync(Guid accountId, AuditFilter filter, int skip, int take, CancellationToken cancellationToken = default)
    {
        var result = Filtered(accountId, filter).OrderByDescending(e => e.OccurredAt).Skip(skip).Take(take).ToArray();
        return Task.FromResult<IReadOnlyList<AuditEvent>>(result);
    }

    public Task<int> CountAsync(Guid accountId, AuditFilter filter, CancellationToken cancellationToken = default)
        => Task.FromResult(Filtered(accountId, filter).Count());

    private IEnumerable<AuditEvent> Filtered(Guid accountId, AuditFilter filter)
    {
        var query = _events.Where(e => e.AccountId == accountId);

        if (filter.Action is not null)
            query = query.Where(e => e.Action.Contains(filter.Action, StringComparison.OrdinalIgnoreCase));
        if (filter.ActionPrefix is not null)
            query = query.Where(e => e.Action.StartsWith(filter.ActionPrefix, StringComparison.OrdinalIgnoreCase));
        if (filter.ApiKeyId is not null)
            query = query.Where(e => e.ApiKeyId == filter.ApiKeyId);

        return query;
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
