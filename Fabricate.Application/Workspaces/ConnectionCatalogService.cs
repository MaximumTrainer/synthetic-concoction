using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Workspaces;

public sealed class ConnectionCatalogService(
    IConnectionRepository connectionRepository,
    IWorkspaceService workspaceService) : IConnectionCatalogService
{
    public async Task<Connection> AddConnectionAsync(Guid workspaceId, string name, string provider, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        await RequireEditorAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        var connection = new Connection(Guid.NewGuid(), workspaceId, name, provider, "active", DateTimeOffset.UtcNow);
        return await connectionRepository.SaveAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Connection> UpdateStatusAsync(Guid connectionId, string status, Guid requestingUserId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await RequireEditorAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);

        var existing = await connectionRepository.GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.WorkspaceId != workspaceId)
        {
            throw new InvalidOperationException($"Connection '{connectionId}' not found.");
        }

        var updated = existing with
        {
            Status = status,
            DisabledAt = status == "disabled" ? DateTimeOffset.UtcNow : null
        };
        return await connectionRepository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveConnectionAsync(Guid connectionId, Guid requestingUserId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await RequireEditorAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);

        var existing = await connectionRepository.GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.WorkspaceId != workspaceId) return;

        await connectionRepository.DeleteAsync(connectionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Connection>> ListAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var role = await workspaceService.GetEffectiveRoleAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (!role.HasValue) throw new UnauthorizedAccessException("Access denied to workspace.");
        return await connectionRepository.ListByWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireEditorAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        var role = await workspaceService.GetEffectiveRoleAsync(workspaceId, userId, cancellationToken).ConfigureAwait(false);
        if (role < WorkspaceRole.Editor)
        {
            throw new UnauthorizedAccessException("Workspace Editor or Admin role required.");
        }
    }
}
