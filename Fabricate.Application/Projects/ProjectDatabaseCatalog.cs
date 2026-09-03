using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Projects;

public sealed class ProjectDatabaseCatalog(IProjectRepository projectRepository, IWorkspaceService workspaceService) : IProjectDatabaseCatalog
{
    private readonly List<ProjectDatabase> _databases = [];

    public async Task<ProjectDatabase> AddAsync(AddDatabaseCommand command, CancellationToken cancellationToken = default)
    {
        var workspaceId = await GetWorkspaceIdOrThrowAsync(command.ProjectId, cancellationToken).ConfigureAwait(false);
        await RequireEditorAsync(workspaceId, command.RequestingUserId, cancellationToken).ConfigureAwait(false);

        var db = new ProjectDatabase(Guid.NewGuid(), command.ProjectId, command.Name, command.Type, command.Provider, "active", command.ConnectionRefId, DateTimeOffset.UtcNow);
        _databases.Add(db);
        return db;
    }

    public async Task<IReadOnlyList<ProjectDatabase>> ListAsync(Guid projectId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var workspaceId = await GetWorkspaceIdOrThrowAsync(projectId, cancellationToken).ConfigureAwait(false);
        var role = await workspaceService.GetEffectiveRoleAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (!role.HasValue) throw new UnauthorizedAccessException("Access denied.");
        return _databases.Where(d => d.ProjectId == projectId).ToArray();
    }

    public async Task RemoveAsync(Guid databaseId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var db = _databases.Find(d => d.Id == databaseId);
        if (db is null) throw new InvalidOperationException($"Database '{databaseId}' not found.");
        var workspaceId = await GetWorkspaceIdOrThrowAsync(db.ProjectId, cancellationToken).ConfigureAwait(false);
        await RequireEditorAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        _databases.RemoveAll(d => d.Id == databaseId);
    }

    private async Task<Guid> GetWorkspaceIdOrThrowAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await projectRepository.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null) throw new InvalidOperationException($"Project '{projectId}' not found.");
        return project.WorkspaceId;
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

