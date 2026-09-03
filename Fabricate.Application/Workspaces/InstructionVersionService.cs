using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Workspaces;

public sealed class InstructionVersionService(IWorkspaceService workspaceService) : IInstructionVersionService
{
    private readonly List<InstructionVersion> _versions = [];
    private readonly List<(Guid ProjectId, InstructionVersion Instruction)> _projectVersions = [];

    public async Task<InstructionVersion> SaveAsync(Guid workspaceId, string content, Guid createdByUserId, CancellationToken cancellationToken = default)
    {
        await RequireEditorAsync(workspaceId, createdByUserId, cancellationToken).ConfigureAwait(false);

        var version = _versions.Where(v => v.WorkspaceId == workspaceId).Select(v => v.Version).DefaultIfEmpty(0).Max() + 1;
        var entry = new InstructionVersion(Guid.NewGuid(), workspaceId, version, content, createdByUserId, DateTimeOffset.UtcNow);
        _versions.Add(entry);
        return entry;
    }

    public Task<InstructionVersion?> GetLatestAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var latest = _versions.Where(v => v.WorkspaceId == workspaceId).OrderByDescending(v => v.Version).FirstOrDefault();
        return Task.FromResult(latest);
    }

    public Task<IReadOnlyList<InstructionVersion>> GetHistoryAsync(Guid workspaceId, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var history = _versions.Where(v => v.WorkspaceId == workspaceId).OrderByDescending(v => v.Version).Take(pageSize).ToArray();
        return Task.FromResult<IReadOnlyList<InstructionVersion>>(history);
    }

    public Task<InstructionVersion> SaveProjectInstructionAsync(Guid projectId, string content, Guid createdByUserId, CancellationToken cancellationToken = default)
    {
        var version = _projectVersions.Where(e => e.ProjectId == projectId).Select(e => e.Instruction.Version).DefaultIfEmpty(0).Max() + 1;
        // Store project instructions against a synthetic WorkspaceId using the projectId so InstructionVersion stays unchanged.
        var entry = new InstructionVersion(Guid.NewGuid(), projectId, version, content, createdByUserId, DateTimeOffset.UtcNow);
        _projectVersions.Add((projectId, entry));
        return Task.FromResult(entry);
    }

    public Task<InstructionVersion?> GetLatestProjectInstructionAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var latest = _projectVersions
            .Where(e => e.ProjectId == projectId)
            .OrderByDescending(e => e.Instruction.Version)
            .Select(e => e.Instruction)
            .FirstOrDefault();
        return Task.FromResult(latest);
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
