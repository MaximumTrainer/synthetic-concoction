using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Governance;

public sealed class AllowedDomainService(
    IAllowedDomainRepository domainRepository,
    IAccountRepository accountRepository,
    IAuditLogService auditLogService) : IAllowedDomainService
{
    public async Task<AllowedDomain> AddDomainAsync(Guid accountId, string domain, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(accountId, requestingUserId, cancellationToken).ConfigureAwait(false);

        var normalised = domain.Trim().ToLowerInvariant();
        var entry = new AllowedDomain(Guid.NewGuid(), accountId, normalised, DateTimeOffset.UtcNow);
        await domainRepository.SaveAsync(entry, cancellationToken).ConfigureAwait(false);

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), accountId, requestingUserId,
            "allowed_domain.added", "AllowedDomain", entry.Id.ToString(),
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        return entry;
    }

    public async Task RemoveDomainAsync(Guid domainId, Guid requestingUserId, Guid accountId, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(accountId, requestingUserId, cancellationToken).ConfigureAwait(false);
        await domainRepository.DeleteAsync(domainId, cancellationToken).ConfigureAwait(false);

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), accountId, requestingUserId,
            "allowed_domain.removed", "AllowedDomain", domainId.ToString(),
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsEmailAllowedAsync(Guid accountId, string email, CancellationToken cancellationToken = default)
    {
        var domains = await domainRepository.ListByAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
        var accountDomains = domains.Select(d => d.Domain).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // If no domains are configured, allow all emails (open account).
        if (accountDomains.Count == 0)
        {
            return true;
        }

        var atIndex = email.IndexOf('@', StringComparison.Ordinal);
        if (atIndex < 0)
        {
            return false;
        }

        var emailDomain = email[(atIndex + 1)..].ToLowerInvariant();
        return accountDomains.Contains(emailDomain);
    }

    public Task<IReadOnlyList<AllowedDomain>> ListDomainsAsync(Guid accountId, CancellationToken cancellationToken = default)
        => domainRepository.ListByAccountAsync(accountId, cancellationToken);

    private async Task RequireOwnerAsync(Guid accountId, Guid userId, CancellationToken cancellationToken)
    {
        var membership = await accountRepository.GetMembershipAsync(accountId, userId, cancellationToken).ConfigureAwait(false);
        if (membership?.Role != AccountRole.Owner)
        {
            throw new UnauthorizedAccessException("Only account owners can manage allowed domains.");
        }
    }
}
