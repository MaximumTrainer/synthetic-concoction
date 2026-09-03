using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Workflows;

public sealed class SkillRegistryService : ISkillRegistry
{
    private readonly List<Skill> _skills = [];

    public Task RegisterSkillAsync(Skill skill, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        _skills.RemoveAll(s => s.Id == skill.Id);
        _skills.Add(skill);
        return Task.CompletedTask;
    }

    public Task<Skill?> GetSkillAsync(Guid skillId, CancellationToken cancellationToken = default)
        => Task.FromResult(_skills.Find(s => s.Id == skillId));

    public Task<IReadOnlyList<Skill>> ListSkillsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Skill>>(_skills.Where(s => s.WorkspaceId == workspaceId && s.IsEnabled).ToArray());

    public Task<bool> IsToolAllowedAsync(Guid skillId, string toolName, CancellationToken cancellationToken = default)
    {
        var skill = _skills.Find(s => s.Id == skillId);
        if (skill is null) return Task.FromResult(false);
        return Task.FromResult(skill.AllowedTools.Contains(toolName, StringComparer.OrdinalIgnoreCase));
    }
}
