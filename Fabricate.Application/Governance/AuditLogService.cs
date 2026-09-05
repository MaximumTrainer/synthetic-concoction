using System.Runtime.CompilerServices;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Governance;

public sealed class AuditLogService(
    IAuditLogRepository auditLogRepository,
    IAccountRepository accountRepository,
    AuditRetentionOptions? retentionOptions = null,
    TimeProvider? timeProvider = null) : IAuditLogService
{
    private readonly AuditRetentionOptions _retention = retentionOptions ?? new AuditRetentionOptions();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        => auditLogRepository.AppendAsync(auditEvent, cancellationToken);

    public async Task<AuditPage> QueryAsync(Guid accountId, int page = 1, int pageSize = 50, string? actionFilter = null, CancellationToken cancellationToken = default)
    {
        var skip = (page - 1) * pageSize;
        var events = await auditLogRepository.QueryAsync(accountId, skip, pageSize, actionFilter, cancellationToken).ConfigureAwait(false);
        var total = await auditLogRepository.CountAsync(accountId, actionFilter, cancellationToken).ConfigureAwait(false);
        return new AuditPage(events, total, page, pageSize);
    }

    public async IAsyncEnumerable<AuditEvent> ExportAsync(
        Guid accountId,
        Guid requestingUserId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Checked before the first row is yielded. The route streams the response, so a failure after the status
        // line has gone out cannot be turned back into a 403.
        var membership = await accountRepository.GetMembershipAsync(accountId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (membership?.Role != AccountRole.Owner)
        {
            throw new UnauthorizedAccessException("Only account owners can export the audit log.");
        }

        await foreach (var auditEvent in auditLogRepository.StreamAsync(accountId, from, to, cancellationToken).ConfigureAwait(false))
        {
            yield return auditEvent with { Details = AuditRedaction.Redact(auditEvent.Details) };
        }
    }

    public async Task<int> ApplyRetentionAsync(CancellationToken cancellationToken = default)
    {
        if (!_retention.IsEnabled) return 0;

        var cutoff = _retention.CutoffFrom(_time.GetUtcNow());

        // Batch until the backlog is clear. A full batch means there is probably more, so keep going; a short
        // one means the window is empty. Cancellation between batches leaves the log consistent — the deletes
        // already committed were all genuinely past the cutoff.
        var deleted = 0;
        int batch;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch = await auditLogRepository
                .DeleteOlderThanAsync(cutoff, _retention.BatchSize, cancellationToken)
                .ConfigureAwait(false);
            deleted += batch;
        }
        while (batch == _retention.BatchSize);

        if (deleted == 0) return 0;

        // The purge is itself an auditable act. It is account-agnostic, so it is recorded against Guid.Empty —
        // recording it per account would mean writing a row for accounts that lost nothing.
        await auditLogRepository.AppendAsync(
            new AuditEvent(
                Guid.NewGuid(),
                Guid.Empty,
                null,
                "audit.retention_applied",
                "AuditEvent",
                null,
                Guid.NewGuid().ToString("N"),
                _time.GetUtcNow(),
                $"retentionDays={_retention.RetentionDays};cutoff={cutoff:O};deleted={deleted}"),
            cancellationToken).ConfigureAwait(false);

        return deleted;
    }
}
