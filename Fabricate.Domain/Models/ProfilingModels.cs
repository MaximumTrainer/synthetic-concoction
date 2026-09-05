namespace Fabricate.Domain.Models;

/// <summary>Aggregate statistics for a single column — no raw sensitive data stored.</summary>
public sealed record ColumnProfile(
    string Name,
    long RowCount,
    long NullCount,
    long DistinctCount,
    string? MinValue,
    string? MaxValue,
    double? MeanNumeric);

/// <summary>Aggregate profile for a single table.</summary>
public sealed record TableProfile(
    string QualifiedName,
    long RowCount,
    IReadOnlyList<ColumnProfile> Columns);

/// <summary>Versioned snapshot of aggregate profiling statistics for a database.
/// Never stores raw row values — only aggregates.</summary>
public sealed record ProfileSnapshot(
    Guid Id,
    string DatabaseName,
    int Version,
    DateTimeOffset CapturedAt,
    IReadOnlyList<TableProfile> Tables,
    Guid WorkspaceId = default);

/// <summary>Versioned snapshot of a database's structural schema.</summary>
public sealed record SchemaSnapshot(
    Guid Id,
    string DatabaseName,
    int Version,
    DateTimeOffset CapturedAt,
    DatabaseSchema Schema,
    Guid WorkspaceId = default);

/// <summary>Diagnostic produced during schema review — surfaces unsupported or partial metadata.</summary>
public sealed record SchemaReviewDiagnostic(
    string Severity,
    string QualifiedName,
    string Message);

/// <summary>Result of a schema review pass — includes the schema, diagnostics, and unknown type warnings.</summary>
public sealed record SchemaReviewReport(
    DatabaseSchema Schema,
    IReadOnlyList<SchemaReviewDiagnostic> Diagnostics,
    IReadOnlyList<string> UnknownSqlTypes)
{
    public bool HasErrors => Diagnostics.Any(static d => d.Severity == "error");
}
