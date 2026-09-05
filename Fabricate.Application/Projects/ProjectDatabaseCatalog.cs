using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Projects;

public sealed class ProjectDatabaseCatalog(
    IProjectDatabaseRepository databaseRepository,
    IProjectRepository projectRepository,
    IWorkspaceService workspaceService) : IProjectDatabaseCatalog
{
    public async Task<ProjectDatabase> AddAsync(AddDatabaseCommand command, CancellationToken cancellationToken = default)
    {
        var workspaceId = await GetWorkspaceIdOrThrowAsync(command.ProjectId, cancellationToken).ConfigureAwait(false);
        await RequireEditorAsync(workspaceId, command.RequestingUserId, cancellationToken).ConfigureAwait(false);

        var db = new ProjectDatabase(Guid.NewGuid(), command.ProjectId, command.Name, command.Type, command.Provider, "active", command.ConnectionRefId, DateTimeOffset.UtcNow);
        return await databaseRepository.SaveAsync(db, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectDatabase>> ListAsync(Guid projectId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var workspaceId = await GetWorkspaceIdOrThrowAsync(projectId, cancellationToken).ConfigureAwait(false);
        var role = await workspaceService.GetEffectiveRoleAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (!role.HasValue) throw new UnauthorizedAccessException("Access denied.");
        return await databaseRepository.ListByProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid databaseId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var db = await databaseRepository.GetByIdAsync(databaseId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Database '{databaseId}' not found.");

        var workspaceId = await GetWorkspaceIdOrThrowAsync(db.ProjectId, cancellationToken).ConfigureAwait(false);
        await RequireEditorAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        await databaseRepository.DeleteAsync(databaseId, cancellationToken).ConfigureAwait(false);
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
        if (role is null or < WorkspaceRole.Editor)
        {
            throw new UnauthorizedAccessException("Workspace Editor or Admin role required.");
        }
    }
}
