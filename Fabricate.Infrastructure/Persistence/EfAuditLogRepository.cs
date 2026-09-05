using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Fabricate.Infrastructure.Persistence;

public sealed class EfAuditLogRepository(FabricateDbContext db) : IAuditLogRepository
{
    public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        db.AuditEvents.Add(auditEvent);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEvent>> QueryAsync(Guid accountId, int skip, int take, string? actionFilter, CancellationToken cancellationToken = default)
    {
        var query = db.AuditEvents.Where(e => e.AccountId == accountId);
        if (actionFilter is not null)
            query = query.Where(e => e.Action.Contains(actionFilter));
        return await query.OrderByDescending(e => e.OccurredAt).Skip(skip).Take(take).ToListAsync(cancellationToken);
    }

    public async Task<int> CountAsync(Guid accountId, string? actionFilter, CancellationToken cancellationToken = default)
    {
        var query = db.AuditEvents.Where(e => e.AccountId == accountId);
        if (actionFilter is not null)
            query = query.Where(e => e.Action.Contains(actionFilter));
        return await query.CountAsync(cancellationToken);
    }

    public IAsyncEnumerable<AuditEvent> StreamAsync(
        Guid accountId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken = default)
    {
        var query = db.AuditEvents.AsNoTracking().Where(e => e.AccountId == accountId);
        if (from is not null) query = query.Where(e => e.OccurredAt >= from);
        if (to is not null) query = query.Where(e => e.OccurredAt <= to);

        // AsAsyncEnumerable keeps the reader open rather than materialising the account's whole log, so an
        // export of a large account streams straight from the database to the response body.
        return query.OrderBy(e => e.OccurredAt).AsAsyncEnumerable();
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, int batchSize, CancellationToken cancellationToken = default)
    {
        // ExecuteDeleteAsync issues one DELETE rather than loading entities to remove them. Both providers
        // reject LIMIT directly on a DELETE, so the batch is expressed as a subquery over the primary key.
        var doomed = db.AuditEvents
            .Where(e => e.OccurredAt < cutoff)
            .OrderBy(e => e.OccurredAt)
            .Take(batchSize)
            .Select(e => e.Id);

        return await db.AuditEvents
            .Where(e => doomed.Contains(e.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
