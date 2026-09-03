using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Repositories;

public sealed class InMemoryAuditLogRepository : IAuditLogRepository
{
    private readonly List<AuditEvent> _events = [];

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
}
