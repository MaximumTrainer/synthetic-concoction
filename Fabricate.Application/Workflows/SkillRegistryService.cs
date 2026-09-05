using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Workflows;

public sealed class SkillRegistryService(ISkillRepository skillRepository) : ISkillRegistry
{
    public async Task RegisterSkillAsync(Skill skill, Guid requestingUserId, CancellationToken cancellationToken = default)
        => await skillRepository.SaveAsync(skill, cancellationToken).ConfigureAwait(false);

    public Task<Skill?> GetSkillAsync(Guid skillId, CancellationToken cancellationToken = default)
        => skillRepository.GetByIdAsync(skillId, cancellationToken);

    public async Task<IReadOnlyList<Skill>> ListSkillsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var skills = await skillRepository.ListByWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return skills.Where(s => s.IsEnabled).ToArray();
    }

    public async Task<bool> IsToolAllowedAsync(Guid skillId, string toolName, CancellationToken cancellationToken = default)
    {
        var skill = await skillRepository.GetByIdAsync(skillId, cancellationToken).ConfigureAwait(false);
        return skill is not null && skill.AllowedTools.Contains(toolName, StringComparer.OrdinalIgnoreCase);
    }
}
