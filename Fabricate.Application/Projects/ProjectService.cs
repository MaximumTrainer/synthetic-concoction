using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Projects;

public sealed class ProjectService(IProjectRepository projectRepository, IWorkspaceService workspaceService, IAuditLogService auditLogService) : IProjectService
{
    public async Task<Project> CreateAsync(CreateProjectCommand command, CancellationToken cancellationToken = default)
    {
        await RequireEditorAsync(command.WorkspaceId, command.CreatedByUserId, cancellationToken).ConfigureAwait(false);

        var project = new Project(Guid.NewGuid(), command.WorkspaceId, command.Name, ProjectStatus.Active, command.CreatedByUserId, DateTimeOffset.UtcNow);
        await projectRepository.SaveAsync(project, cancellationToken).ConfigureAwait(false);

        var workspace = await workspaceService.GetByIdAsync(command.WorkspaceId, command.CreatedByUserId, cancellationToken).ConfigureAwait(false);
        if (workspace is not null)
        {
            await auditLogService.RecordAsync(new AuditEvent(
                Guid.NewGuid(), workspace.AccountId, command.CreatedByUserId,
                "project.created", "Project", project.Id.ToString(),
                Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }

        return project;
    }

    public async Task<Project> RenameAsync(Guid projectId, string newName, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectOrThrowAsync(projectId, requestingUserId, cancellationToken).ConfigureAwait(false);
        await RequireEditorAsync(project.WorkspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);

        var updated = project with { Name = newName };
        await projectRepository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task<Project> ArchiveAsync(Guid projectId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var project = await GetProjectOrThrowAsync(projectId, requestingUserId, cancellationToken).ConfigureAwait(false);
        await RequireEditorAsync(project.WorkspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);

        var archived = project with { Status = ProjectStatus.Archived, ArchivedAt = DateTimeOffset.UtcNow };
        await projectRepository.SaveAsync(archived, cancellationToken).ConfigureAwait(false);

        var workspace = await workspaceService.GetByIdAsync(project.WorkspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (workspace is not null)
        {
            await auditLogService.RecordAsync(new AuditEvent(
                Guid.NewGuid(), workspace.AccountId, requestingUserId,
                "project.archived", "Project", project.Id.ToString(),
                Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        }

        return archived;
    }

    public async Task<Project?> GetByIdAsync(Guid projectId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var project = await projectRepository.GetByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project is null) return null;

        var role = await workspaceService.GetEffectiveRoleAsync(project.WorkspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        return role.HasValue ? project : null;
    }

    public async Task<IReadOnlyList<Project>> ListAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var role = await workspaceService.GetEffectiveRoleAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (!role.HasValue) throw new UnauthorizedAccessException("Access denied to workspace.");
        return await projectRepository.ListByWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Project> GetProjectOrThrowAsync(Guid projectId, Guid requestingUserId, CancellationToken cancellationToken)
    {
        var project = await GetByIdAsync(projectId, requestingUserId, cancellationToken).ConfigureAwait(false);
        return project ?? throw new InvalidOperationException($"Project '{projectId}' not found or access denied.");
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

