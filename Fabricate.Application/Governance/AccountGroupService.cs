using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Governance;

public sealed class AccountGroupService(
    IAccountGroupRepository groupRepository,
    IAccountRepository accountRepository,
    IAuditLogService auditLogService) : IAccountGroupService
{
    public async Task<AccountGroup> CreateGroupAsync(Guid accountId, string name, Guid createdByUserId, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(accountId, createdByUserId, cancellationToken).ConfigureAwait(false);

        var group = new AccountGroup(Guid.NewGuid(), accountId, name, DateTimeOffset.UtcNow);
        await groupRepository.SaveAsync(group, cancellationToken).ConfigureAwait(false);

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), accountId, createdByUserId,
            "group.created", "AccountGroup", group.Id.ToString(),
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        return group;
    }

    public async Task AddGroupMemberAsync(Guid groupId, Guid userId, Guid requestingUserId, Guid accountId, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(accountId, requestingUserId, cancellationToken).ConfigureAwait(false);
        await groupRepository.AddMemberAsync(new GroupMembership(groupId, userId, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), accountId, requestingUserId,
            "group.member_added", "GroupMembership", $"{groupId}/{userId}",
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveGroupMemberAsync(Guid groupId, Guid userId, Guid requestingUserId, Guid accountId, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(accountId, requestingUserId, cancellationToken).ConfigureAwait(false);
        await groupRepository.RemoveMemberAsync(groupId, userId, cancellationToken).ConfigureAwait(false);

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), accountId, requestingUserId,
            "group.member_removed", "GroupMembership", $"{groupId}/{userId}",
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<AccountGroup>> ListGroupsAsync(Guid accountId, CancellationToken cancellationToken = default)
        => groupRepository.ListByAccountAsync(accountId, cancellationToken);

    private async Task RequireOwnerAsync(Guid accountId, Guid userId, CancellationToken cancellationToken)
    {
        var membership = await accountRepository.GetMembershipAsync(accountId, userId, cancellationToken).ConfigureAwait(false);
        if (membership?.Role != AccountRole.Owner)
        {
            throw new UnauthorizedAccessException("Only account owners can manage groups.");
        }
    }
}
