using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Workspaces;

public sealed class WorkspaceService(IAuditLogService auditLogService) : IWorkspaceService
{
    private readonly List<Workspace> _workspaces = [];
    private readonly List<WorkspaceMembership> _memberships = [];

    public async Task<Workspace> CreateAsync(CreateWorkspaceCommand command, CancellationToken cancellationToken = default)
    {
        var workspace = new Workspace(Guid.NewGuid(), command.AccountId, command.Name, DateTimeOffset.UtcNow);
        _workspaces.Add(workspace);

        _memberships.Add(new WorkspaceMembership(workspace.Id, command.CreatedByUserId, false, WorkspaceRole.Admin, DateTimeOffset.UtcNow));

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), command.AccountId, command.CreatedByUserId,
            "workspace.created", "Workspace", workspace.Id.ToString(),
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        return workspace;
    }

    public async Task<Workspace?> GetByIdAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var workspace = _workspaces.Find(w => w.Id == workspaceId);
        if (workspace is null) return null;

        var role = await GetEffectiveRoleAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        return role.HasValue ? workspace : null;
    }

    public async Task GrantAccessAsync(GrantWorkspaceAccessCommand command, CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(command.WorkspaceId, command.RequestingUserId, cancellationToken).ConfigureAwait(false);

        var existing = _memberships.FindIndex(m => m.WorkspaceId == command.WorkspaceId && m.PrincipalId == command.PrincipalId && m.IsGroup == command.IsGroup);
        var membership = new WorkspaceMembership(command.WorkspaceId, command.PrincipalId, command.IsGroup, command.Role, DateTimeOffset.UtcNow);

        if (existing >= 0)
        {
            _memberships[existing] = membership;
        }
        else
        {
            _memberships.Add(membership);
        }

        var workspace = _workspaces.Find(w => w.Id == command.WorkspaceId);
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
        _memberships.RemoveAll(m => m.WorkspaceId == workspaceId && m.PrincipalId == principalId && m.IsGroup == isGroup);
    }

    public Task<WorkspaceRole?> GetEffectiveRoleAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken = default)
    {
        var direct = _memberships.FirstOrDefault(m => m.WorkspaceId == workspaceId && m.PrincipalId == userId && !m.IsGroup);
        WorkspaceRole? role = direct is not null ? direct.Role : null;
        return Task.FromResult(role);
    }

    private async Task RequireAdminAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        var role = await GetEffectiveRoleAsync(workspaceId, userId, cancellationToken).ConfigureAwait(false);
        if (role < WorkspaceRole.Admin)
        {
            throw new UnauthorizedAccessException("Only workspace admins can manage workspace access.");
        }
    }
}
