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

/// <summary>Whether an operation returns a collection or a single item, decided from its path and schema.</summary>
public enum GeneratedResponseKind
{
    /// <summary>A list operation: the bound table's rows, as an array.</summary>
    Collection = 0,

    /// <summary>An item operation: one row, matched on the trailing path parameter.</summary>
    Item,
}

/// <summary>
/// An API endpoint generated from an OpenAPI contract, backed by a generated dataset artifact (#70).
/// </summary>
/// <param name="BoundTable">The qualified table in the run whose rows this endpoint serves, once bound.</param>
/// <param name="ResponseSchemaJson">
/// The operation's 2xx response schema, kept so a payload can be checked against the contract before it is
/// served rather than after someone's client rejects it.
/// </param>
/// <param name="Diagnostics">
/// Why the endpoint cannot be served, when it cannot. A schema mismatch belongs here rather than in a 500: the
/// endpoint is misconfigured, which is a fact about the endpoint, not a failure of the request.
/// </param>
public sealed record GeneratedApiEndpoint(
    Guid Id,
    Guid WorkspaceId,
    string Path,
    string Method,
    string OperationId,
    Guid? ArtifactRunId,
    bool IsActive,
    DateTimeOffset CreatedAt,
    Guid? ContractId = null,
    string? BoundTable = null,
    GeneratedResponseKind ResponseKind = GeneratedResponseKind.Collection,
    string? ResponseSchemaJson = null,
    string? Diagnostics = null)
{
    /// <summary>An endpoint is servable only when active, bound to a run and table, and free of diagnostics.</summary>
    public bool IsServable => IsActive
        && ArtifactRunId is not null
        && !string.IsNullOrWhiteSpace(BoundTable)
        && string.IsNullOrWhiteSpace(Diagnostics);
}

/// <summary>An ingested OpenAPI contract. The document is kept so endpoints can be re-derived from it.</summary>
public sealed record ApiContract(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    string Version,
    string DocumentJson,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAt);
