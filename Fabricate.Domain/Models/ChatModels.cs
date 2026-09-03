namespace Fabricate.Domain.Models;

public enum ChatMode
{
    Guided = 0,
    Autonomous,
    ReviewRequired
}

public enum InstructionScope
{
    Workspace = 0,
    Project,
    Chat
}

public enum MessageRole
{
    User = 0,
    Assistant,
    System,
    Tool
}

public enum ToolInvocationStatus
{
    Pending = 0,
    Running,
    Succeeded,
    Failed
}

public sealed record ChatSession(
    Guid Id,
    Guid WorkspaceId,
    Guid? ProjectId,
    Guid UserId,
    string Name,
    ChatMode Mode,
    bool IsArchived,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt = null,
    string? InstructionOverride = null);

public sealed record ChatMessage(
    Guid Id,
    Guid SessionId,
    MessageRole Role,
    string Content,
    DateTimeOffset CreatedAt);

public sealed record Instruction(
    Guid Id,
    Guid WorkspaceId,
    Guid? ProjectId,
    Guid? SessionId,
    InstructionScope Scope,
    string Content,
    int Version,
    DateTimeOffset CreatedAt);

public sealed record ToolInvocation(
    Guid Id,
    Guid SessionId,
    Guid? MessageId,
    string ToolName,
    string? InputJson,
    string? OutputJson,
    ToolInvocationStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt = null,
    string? ErrorMessage = null);
