using System.Collections.Concurrent;
using Fabricate.Application.Abstractions;

namespace Fabricate.Application.Chat;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<string>> _allowlists = new();

    public void Register(ITool tool) => _tools[tool.Name] = tool;

    public ITool? Resolve(string toolName) => _tools.GetValueOrDefault(toolName);

    /// <summary>Every registered tool unless the workspace has an explicit allowlist, in which case only its members.</summary>
    public IReadOnlyList<string> AllowedTools(Guid workspaceId)
    {
        if (_allowlists.TryGetValue(workspaceId, out var allowlist))
        {
            return allowlist.Where(_tools.ContainsKey).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        return _tools.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void SetAllowedTools(Guid workspaceId, IReadOnlyList<string> toolNames)
        => _allowlists[workspaceId] = toolNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
