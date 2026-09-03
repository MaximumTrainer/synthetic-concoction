using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Governance;

public sealed class AuditLogService(IAuditLogRepository auditLogRepository) : IAuditLogService
{
    public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        => auditLogRepository.AppendAsync(auditEvent, cancellationToken);

    public async Task<AuditPage> QueryAsync(Guid accountId, int page = 1, int pageSize = 50, string? actionFilter = null, CancellationToken cancellationToken = default)
    {
        var skip = (page - 1) * pageSize;
        var events = await auditLogRepository.QueryAsync(accountId, skip, pageSize, actionFilter, cancellationToken).ConfigureAwait(false);
        var total = await auditLogRepository.CountAsync(accountId, actionFilter, cancellationToken).ConfigureAwait(false);
        return new AuditPage(events, total, page, pageSize);
    }
}
