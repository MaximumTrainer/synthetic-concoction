using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Llm;

/// <summary>
/// Provider-neutral token estimate: roughly four characters per token for English text and JSON, plus a fixed
/// per-message overhead. Deliberately conservative — it exists to keep requests under a budget, not to bill.
/// Provider tokenizers differ (the Opus 4.7+ tokenizer runs up to ~1.35× the older one), so the budget should
/// leave headroom below the model's context window.
/// </summary>
public sealed class HeuristicTokenBudgetEstimator : ITokenBudgetEstimator
{
    private const int CharsPerToken = 4;
    private const int PerMessageOverhead = 4;
    private const int PerToolOverhead = 12;

    public int Estimate(ChatCompletionRequest request)
    {
        var total = Chars(request.SystemInstructions) / CharsPerToken;

        foreach (var tool in request.Tools)
        {
            total += PerToolOverhead + (Chars(tool.Name) + Chars(tool.Description) + Chars(tool.InputSchemaJson)) / CharsPerToken;
        }

        foreach (var message in request.Messages)
        {
            total += Estimate(message);
        }

        return total;
    }

    public int Estimate(LlmMessage message)
    {
        var chars = Chars(message.Text);

        if (message.ToolCalls is not null)
        {
            foreach (var call in message.ToolCalls)
            {
                chars += Chars(call.Id) + Chars(call.Name) + Chars(call.ArgumentsJson);
            }
        }

        if (message.ToolResult is not null)
        {
            chars += Chars(message.ToolResult.ToolCallId) + Chars(message.ToolResult.Content);
        }

        return PerMessageOverhead + (chars + CharsPerToken - 1) / CharsPerToken;
    }

    private static int Chars(string? value) => value?.Length ?? 0;
}
