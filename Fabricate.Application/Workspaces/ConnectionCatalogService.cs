using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Workspaces;

public sealed class ConnectionCatalogService(IWorkspaceService workspaceService) : IConnectionCatalogService
{
    private readonly List<Connection> _connections = [];

    public async Task<Connection> AddConnectionAsync(Guid workspaceId, string name, string provider, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        await RequireEditorAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        var connection = new Connection(Guid.NewGuid(), workspaceId, name, provider, "active", DateTimeOffset.UtcNow);
        _connections.Add(connection);
        return connection;
    }

    public async Task<Connection> UpdateStatusAsync(Guid connectionId, string status, Guid requestingUserId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await RequireEditorAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        var index = _connections.FindIndex(c => c.Id == connectionId && c.WorkspaceId == workspaceId);
        if (index < 0) throw new InvalidOperationException($"Connection '{connectionId}' not found.");
        var updated = _connections[index] with
        {
            Status = status,
            DisabledAt = status == "disabled" ? DateTimeOffset.UtcNow : null
        };
        _connections[index] = updated;
        return updated;
    }

    public async Task RemoveConnectionAsync(Guid connectionId, Guid requestingUserId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        await RequireEditorAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        _connections.RemoveAll(c => c.Id == connectionId && c.WorkspaceId == workspaceId);
    }

    public async Task<IReadOnlyList<Connection>> ListAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var role = await workspaceService.GetEffectiveRoleAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (!role.HasValue) throw new UnauthorizedAccessException("Access denied to workspace.");
        return _connections.Where(c => c.WorkspaceId == workspaceId).ToArray();
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
