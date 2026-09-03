using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Governance;

public sealed class AccountGroupService(
    IAccountRepository accountRepository,
    IAuditLogService auditLogService) : IAccountGroupService
{
    private readonly List<AccountGroup> _groups = [];
    private readonly List<GroupMembership> _memberships = [];

    public async Task<AccountGroup> CreateGroupAsync(Guid accountId, string name, Guid createdByUserId, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(accountId, createdByUserId, cancellationToken).ConfigureAwait(false);

        var group = new AccountGroup(Guid.NewGuid(), accountId, name, DateTimeOffset.UtcNow);
        _groups.Add(group);

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), accountId, createdByUserId,
            "group.created", "AccountGroup", group.Id.ToString(),
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        return group;
    }

    public async Task AddGroupMemberAsync(Guid groupId, Guid userId, Guid requestingUserId, Guid accountId, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(accountId, requestingUserId, cancellationToken).ConfigureAwait(false);
        _memberships.Add(new GroupMembership(groupId, userId, DateTimeOffset.UtcNow));

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), accountId, requestingUserId,
            "group.member_added", "GroupMembership", $"{groupId}/{userId}",
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveGroupMemberAsync(Guid groupId, Guid userId, Guid requestingUserId, Guid accountId, CancellationToken cancellationToken = default)
    {
        await RequireOwnerAsync(accountId, requestingUserId, cancellationToken).ConfigureAwait(false);
        _memberships.RemoveAll(m => m.GroupId == groupId && m.UserId == userId);

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), accountId, requestingUserId,
            "group.member_removed", "GroupMembership", $"{groupId}/{userId}",
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<AccountGroup>> ListGroupsAsync(Guid accountId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AccountGroup>>(_groups.Where(g => g.AccountId == accountId).ToArray());

    private async Task RequireOwnerAsync(Guid accountId, Guid userId, CancellationToken cancellationToken)
    {
        var membership = await accountRepository.GetMembershipAsync(accountId, userId, cancellationToken).ConfigureAwait(false);
        if (membership?.Role != AccountRole.Owner)
        {
            throw new UnauthorizedAccessException("Only account owners can manage groups.");
        }
    }
}
