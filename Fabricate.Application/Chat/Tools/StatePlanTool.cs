using System.Text.Json;
using Fabricate.Application.Abstractions;
using Fabricate.Application.Llm;

namespace Fabricate.Application.Chat.Tools;

/// <summary>
/// Lets the agent state, and later revise, the plan it intends to follow (#87).
///
/// <para>
/// A plan is a tool call rather than prose so that it lands in the same places every other call does: a
/// <c>ToolInvocation</c> row carrying the steps, a Tool message in the conversation, and an audit event. That is
/// what makes "plan revisions are auditable" true — a revision sits next to the calls it governs and in the same
/// order, instead of being buried in an assistant message someone has to read to find.
/// </para>
///
/// <para>
/// It changes nothing, which is the point: the agent can be required to call it before the first mutating call
/// without that requirement itself being a risk.
/// </para>
/// </summary>
public sealed class StatePlanTool : ITool
{
    public string Name => AgentPromptGuidance.PlanToolName;

    public string Description =>
        "State the steps you intend to take before the first call that generates or changes data. Call again " +
        "with revised steps if you change course, saying what changed and why.";

    /// <summary>Steps and an optional revision reason; nothing here is a value from the customer's data.</summary>
    public PromptContentClass ContentClass => PromptContentClass.Metadata;

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "steps": {
              "type": "array",
              "description": "The steps you intend to take, in order, each one short and concrete.",
              "items": { "type": "string" }
            },
            "revises": {
              "type": "string",
              "description": "When revising an earlier plan, what changed and why. Omit for a first plan."
            }
          },
          "required": ["steps"]
        }
        """;

    public Task<string> ExecuteAsync(string inputJson, Guid sessionId, Guid userId, CancellationToken cancellationToken = default)
    {
        string[] steps;
        string? revises;

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
            var root = document.RootElement;

            steps = root.TryGetProperty("steps", out var stepsElement) && stepsElement.ValueKind == JsonValueKind.Array
                ? stepsElement.EnumerateArray().Select(e => e.GetString()).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToArray()
                : [];

            revises = root.TryGetProperty("revises", out var revisesElement) && revisesElement.ValueKind == JsonValueKind.String
                ? revisesElement.GetString()
                : null;
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"The plan could not be read as JSON: {ex.Message}", nameof(inputJson));
        }

        if (steps.Length == 0)
        {
            throw new ArgumentException("A plan needs at least one step.", nameof(inputJson));
        }

        // Echoed back so the plan appears in the conversation the model sees, which is what lets it revise its
        // own plan later rather than restating one it has forgotten.
        return Task.FromResult(JsonSerializer.Serialize(
            new
            {
                acknowledged = true,
                isRevision = revises is not null,
                steps,
                revises,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
