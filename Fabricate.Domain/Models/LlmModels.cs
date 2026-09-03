namespace Fabricate.Domain.Models;

/// <summary>Model providers Fabricate can talk to. Cloud-hosted Claude variants authenticate with cloud identity rather than an API key.</summary>
public enum LlmProvider
{
    Anthropic = 0,
    OpenAiCompatible,
    AwsBedrock,
    GcpVertexAi,
    AzureFoundry
}

public enum LlmStopReason
{
    EndTurn = 0,
    ToolUse,
    MaxTokens,
    /// <summary>The provider declined the request. Returned as a normal response, never as an exception.</summary>
    Refusal,
    ContentFiltered,
    Error
}

public enum LlmEffort
{
    Low = 0,
    Medium,
    High,
    Max
}

public enum LlmMessageRole
{
    User = 0,
    Assistant,
    Tool
}

/// <summary>A tool the model may call. <see cref="InputSchemaJson"/> is a JSON Schema object.</summary>
public sealed record LlmToolDefinition(string Name, string Description, string InputSchemaJson);

/// <summary>A tool call requested by the model. <see cref="ArgumentsJson"/> is the raw JSON object the model produced.</summary>
public sealed record LlmToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>The outcome of executing a tool call, fed back to the model.</summary>
public sealed record LlmToolResult(string ToolCallId, string Content, bool IsError = false);

/// <summary>One provider-neutral conversation turn.</summary>
public sealed record LlmMessage(
    LlmMessageRole Role,
    string? Text = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    LlmToolResult? ToolResult = null)
{
    public static LlmMessage User(string text) => new(LlmMessageRole.User, text);

    public static LlmMessage Assistant(string? text, IReadOnlyList<LlmToolCall>? toolCalls = null)
        => new(LlmMessageRole.Assistant, text, toolCalls);

    public static LlmMessage FromToolResult(LlmToolResult result) => new(LlmMessageRole.Tool, ToolResult: result);
}

/// <summary>
/// What a provider/model pair actually accepts. Providers disagree on request shape and reject
/// unknown parameters with hard errors, so the orchestrator consults this before building a request.
/// </summary>
public sealed record ModelCapabilities(
    bool SupportsSampling,
    bool SupportsStreaming,
    bool SupportsToolCalling,
    bool SupportsEffort,
    bool SupportsStructuredOutput,
    int MaxOutputTokens);

public sealed record ChatCompletionRequest(
    string Model,
    string? SystemInstructions,
    IReadOnlyList<LlmMessage> Messages,
    IReadOnlyList<LlmToolDefinition> Tools,
    int MaxOutputTokens,
    double? Temperature = null,
    LlmEffort? Effort = null);

public sealed record TokenUsage(int InputTokens, int OutputTokens)
{
    public static readonly TokenUsage Zero = new(0, 0);
    public int TotalTokens => InputTokens + OutputTokens;
    public TokenUsage Add(TokenUsage other) => new(InputTokens + other.InputTokens, OutputTokens + other.OutputTokens);
}

public sealed record ChatCompletionResult(
    string? Text,
    IReadOnlyList<LlmToolCall> ToolCalls,
    LlmStopReason StopReason,
    TokenUsage Usage,
    string ModelId,
    string? StopDetail = null);

/// <summary>A streaming increment. Exactly one chunk per stream carries <see cref="Final"/>.</summary>
public sealed record ChatCompletionChunk(string? TextDelta, ChatCompletionResult? Final = null);
