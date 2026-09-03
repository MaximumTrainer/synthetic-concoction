namespace Fabricate.Domain.Models;

public enum WorkflowStatus
{
    Active = 0,
    Disabled,
    Archived
}

public enum WorkflowRunStatus
{
    Queued = 0,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed record Workflow(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    int Version,
    WorkflowStatus Status,
    DateTimeOffset CreatedAt);

public sealed record WorkflowStep(
    Guid Id,
    Guid WorkflowId,
    int StepOrder,
    string StepType,
    string? Configuration);

public sealed record WorkflowRun(
    Guid Id,
    Guid WorkflowId,
    WorkflowRunStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? FailureReason = null);

public sealed record WorkflowStepRun(
    Guid Id,
    Guid WorkflowRunId,
    Guid StepId,
    int StepOrder,
    WorkflowRunStatus Status,
    int RetryCount,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? FailureReason = null);

/// <summary>A named skill with allowlisted tool access and workspace-scoped permissions.</summary>
public sealed record Skill(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    string Description,
    IReadOnlyList<string> AllowedTools,
    bool IsEnabled,
    DateTimeOffset CreatedAt);

/// <summary>An API endpoint generated from an OpenAPI contract, backed by a generated dataset artifact.</summary>
public sealed record GeneratedApiEndpoint(
    Guid Id,
    Guid WorkspaceId,
    string Path,
    string Method,
    string OperationId,
    Guid? ArtifactRunId,
    bool IsActive,
    DateTimeOffset CreatedAt);
