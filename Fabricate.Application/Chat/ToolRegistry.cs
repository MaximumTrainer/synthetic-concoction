using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Chat;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ITool tool) => _tools[tool.Name] = tool;

    public ITool? Resolve(string toolName) => _tools.GetValueOrDefault(toolName);

    public IReadOnlyList<string> AllowedTools(Guid workspaceId) => _tools.Keys.Order().ToArray();
}
