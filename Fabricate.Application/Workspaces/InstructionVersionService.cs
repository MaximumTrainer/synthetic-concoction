using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Workspaces;

public sealed class InstructionVersionService(
    IInstructionVersionRepository instructionRepository,
    IWorkspaceService workspaceService) : IInstructionVersionService
{
    public async Task<InstructionVersion> SaveAsync(Guid workspaceId, string content, Guid createdByUserId, CancellationToken cancellationToken = default)
    {
        await RequireEditorAsync(workspaceId, createdByUserId, cancellationToken).ConfigureAwait(false);

        var existing = await instructionRepository.ListByWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var version = existing.Select(v => v.Version).DefaultIfEmpty(0).Max() + 1;

        var entry = new InstructionVersion(Guid.NewGuid(), workspaceId, version, content, createdByUserId, DateTimeOffset.UtcNow);
        return await instructionRepository.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InstructionVersion?> GetLatestAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var versions = await instructionRepository.ListByWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return versions.OrderByDescending(v => v.Version).FirstOrDefault();
    }

    public async Task<IReadOnlyList<InstructionVersion>> GetHistoryAsync(Guid workspaceId, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var versions = await instructionRepository.ListByWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return versions.OrderByDescending(v => v.Version).Take(pageSize).ToArray();
    }

    public async Task<InstructionVersion> SaveProjectInstructionAsync(Guid projectId, string content, Guid createdByUserId, CancellationToken cancellationToken = default)
    {
        var existing = await instructionRepository.ListByProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var version = existing.Select(v => v.Version).DefaultIfEmpty(0).Max() + 1;

        // WorkspaceId is empty because this API does not receive one; the project id is the scope. See InstructionVersion.
        var entry = new InstructionVersion(Guid.NewGuid(), Guid.Empty, version, content, createdByUserId, DateTimeOffset.UtcNow, projectId);
        return await instructionRepository.SaveAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InstructionVersion?> GetLatestProjectInstructionAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var versions = await instructionRepository.ListByProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        return versions.OrderByDescending(v => v.Version).FirstOrDefault();
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
