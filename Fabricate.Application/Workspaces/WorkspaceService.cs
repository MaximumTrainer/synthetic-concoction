using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Workspaces;

public sealed class WorkspaceService(
    IWorkspaceRepository workspaceRepository,
    IAccountGroupRepository accountGroupRepository,
    IAuditLogService auditLogService) : IWorkspaceService
{
    public async Task<Workspace> CreateAsync(CreateWorkspaceCommand command, CancellationToken cancellationToken = default)
    {
        var workspace = new Workspace(Guid.NewGuid(), command.AccountId, command.Name, DateTimeOffset.UtcNow);
        await workspaceRepository.SaveAsync(workspace, cancellationToken).ConfigureAwait(false);

        await workspaceRepository.SaveMembershipAsync(
            new WorkspaceMembership(workspace.Id, command.CreatedByUserId, false, WorkspaceRole.Admin, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), command.AccountId, command.CreatedByUserId,
            "workspace.created", "Workspace", workspace.Id.ToString(),
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        return workspace;
    }

    public async Task<Workspace?> GetByIdAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var workspace = await workspaceRepository.GetByIdAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace is null) return null;

        var role = await GetEffectiveRoleAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        return role.HasValue ? workspace : null;
    }

    public async Task GrantAccessAsync(GrantWorkspaceAccessCommand command, CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(command.WorkspaceId, command.RequestingUserId, cancellationToken).ConfigureAwait(false);

        await workspaceRepository.SaveMembershipAsync(
            new WorkspaceMembership(command.WorkspaceId, command.PrincipalId, command.IsGroup, command.Role, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);

        var workspace = await workspaceRepository.GetByIdAsync(command.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace is not null)
        {
            await auditLogService.RecordAsync(new AuditEvent(
                Guid.NewGuid(), workspace.AccountId, command.RequestingUserId,
                "workspace.access_granted", "WorkspaceMembership", $"{command.WorkspaceId}/{command.PrincipalId}",
                Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RevokeAccessAsync(Guid workspaceId, Guid principalId, bool isGroup, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        await workspaceRepository.RemoveMembershipAsync(workspaceId, principalId, isGroup, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The highest role the user holds on the workspace, whether granted directly or through an account group
    /// they belong to (#67). Returns null when they hold none.
    /// </summary>
    public async Task<WorkspaceRole?> GetEffectiveRoleAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default)
    {
        var memberships = await workspaceRepository.ListMembershipsAsync(workspaceId, cancellationToken).ConfigureAwait(false);

        WorkspaceRole? effective = null;
        foreach (var membership in memberships.Where(m => !m.IsGroup && m.PrincipalId == userId))
        {
            effective = Max(effective, membership.Role);
        }

        var groupGrants = memberships.Where(m => m.IsGroup).ToArray();
        if (groupGrants.Length > 0)
        {
            var userGroups = await accountGroupRepository.ListGroupIdsForUserAsync(userId, cancellationToken).ConfigureAwait(false);
            foreach (var grant in groupGrants.Where(g => userGroups.Contains(g.PrincipalId)))
            {
                effective = Max(effective, grant.Role);
            }
        }

        return effective;
    }

    private static WorkspaceRole Max(WorkspaceRole? current, WorkspaceRole candidate)
        => current is null || candidate > current ? candidate : current.Value;

    private async Task RequireAdminAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        var role = await GetEffectiveRoleAsync(workspaceId, userId, cancellationToken).ConfigureAwait(false);
        if (role < WorkspaceRole.Admin)
        {
            throw new UnauthorizedAccessException("Only workspace admins can manage workspace access.");
        }
    }
}
