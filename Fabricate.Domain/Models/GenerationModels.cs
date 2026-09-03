using Fabricate.Domain.Enums;

namespace Fabricate.Domain.Models;

public sealed record GenerationRequest(
    DatabaseSchema Schema,
    IReadOnlyDictionary<string, int> RequestedRowCounts,
    long Seed,
    RuleConfiguration? Rules = null,
    ComplianceProfile ComplianceProfile = ComplianceProfile.Default);

public sealed record TableData(string Table, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

public sealed record GenerationResult(
    IReadOnlyList<TableData> Tables,
    IReadOnlyList<ValidationIssue> ValidationIssues,
    IReadOnlyList<ComplianceDecision> ComplianceDecisions)
{
    public bool IsSuccess => ValidationIssues.Count == 0;
}

public sealed record ValidationIssue(string Table, string Column, string Reason);

public sealed record GenerationPlan(
    IReadOnlyList<string> OrderedTables,
    IReadOnlyList<IReadOnlyList<string>> Cycles,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<string> SelfReferencingTables)
{
    public GenerationPlan(IReadOnlyList<string> orderedTables, IReadOnlyList<IReadOnlyList<string>> cycles, IReadOnlyList<string> diagnostics)
        : this(orderedTables, cycles, diagnostics, []) { }
}

public sealed record RunSummary(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int TableCount,
    int RowCount,
    int ValidationIssueCount,
    IReadOnlyList<string> Messages);

/// <summary>Records the compliance decision made for a single column, including the source of the strategy.</summary>
public sealed record ComplianceDecision(
    string Table,
    string Column,
    SensitiveFieldStrategy Strategy,
    string Classification,
    string Reason,
    StrategySource Source = StrategySource.None);

/// <summary>Run lifecycle entity tracking status and reproducibility metadata for a generation run.</summary>
public sealed record DatasetRun(
    Guid Id,
    RunStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    long Seed,
    Guid? SchemaSnapshotId,
    Guid? ProfileSnapshotId,
    IReadOnlyDictionary<string, int> RequestedRowCounts,
    IReadOnlyDictionary<string, string>? ArtifactChecksums = null,
    IReadOnlyList<string>? ArtifactPaths = null,
    int ValidationIssueCount = 0,
    string? FailureReason = null,
    Guid? WorkspaceId = null);

/// <summary>Manifest capturing full reproducibility and lineage metadata for a completed run.</summary>
public sealed record RunManifest(
    Guid RunId,
    long Seed,
    Guid? SchemaSnapshotId,
    Guid? ProfileSnapshotId,
    IReadOnlyDictionary<string, int> RequestedRowCounts,
    IReadOnlyDictionary<string, int> ActualRowCounts,
    int ValidationIssueCount,
    IReadOnlyDictionary<string, string> ArtifactChecksums,
    IReadOnlyList<string> ArtifactPaths,
    DateTimeOffset GeneratedAt);
