using System.ComponentModel.DataAnnotations;

namespace Fabricate.Domain.Models;

public sealed record RuleConfiguration
{
    [Required]
    public required string Version { get; init; } = "1";

    [MinLength(0)]
    public IReadOnlyList<TableRule> Tables { get; init; } = [];
}

public sealed record TableRule
{
    [Required]
    public required string Table { get; init; }

    /// <summary>
    /// Overrides the global row count for this specific table.
    /// When set, takes precedence over <c>--rows</c> / <c>RequestedRowCounts</c>.
    /// Must be ≥ 1 when specified.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "RowCount must be at least 1 when specified.")]
    public int? RowCount { get; init; }

    public IReadOnlyList<ColumnRule> Columns { get; init; } = [];
}

public sealed record ColumnRule
{
    [Required]
    public required string Column { get; init; }

    public string? Strategy { get; init; }

    public object? FixedValue { get; init; }

    [Range(0, 1)]
    public double? NullRate { get; init; }

    public int? SeedOffset { get; init; }

    public Dictionary<string, double>? Distribution { get; init; }

    /// <summary>
    /// Minimum value (inclusive) for numeric or date/datetime columns.
    /// For numeric columns: a parseable number. For date/datetime: ISO-8601 string.
    /// Silently ignored for types that don't support range constraints.
    /// </summary>
    public string? MinValue { get; init; }

    /// <summary>
    /// Maximum value (inclusive) for numeric or date/datetime columns.
    /// For numeric columns: a parseable number. For date/datetime: ISO-8601 string.
    /// Silently ignored for types that don't support range constraints.
    /// </summary>
    public string? MaxValue { get; init; }

    /// <summary>
    /// Regex-like character class pattern for string columns, e.g. "[A-Z]{3}[0-9]{4}".
    /// Supported classes: [A-Z], [a-z], [0-9], [A-Za-z0-9]. Literal chars allowed.
    /// Silently ignored for non-string columns.
    /// </summary>
    public string? Pattern { get; init; }

    /// <summary>
    /// Path-level strategy overrides for JSON/JSONB columns.
    /// Each entry targets a dot-notation path within the JSON document (e.g. "$.address.city").
    /// </summary>
    public IReadOnlyList<JsonPathRule>? JsonPaths { get; init; }
}

/// <summary>
/// Assigns a generation strategy to a specific path within a JSON column.
/// Path uses dollar-dot notation: "$.field" or "$.parent.child".
/// Array indexing (e.g. "$.items[0]") is not supported.
/// </summary>
public sealed record JsonPathRule
{
    /// <summary>
    /// Dollar-dot path within the JSON document, e.g. "$.email" or "$.address.city".
    /// </summary>
    [Required]
    public required string Path { get; init; }

    /// <summary>
    /// DataKind name to use for generation (e.g. "Email", "Integer", "Name").
    /// Defaults to "String" when omitted or unrecognised.
    /// </summary>
    public string? Strategy { get; init; }

    /// <summary>Fixed value emitted verbatim for this path; overrides Strategy.</summary>
    public object? FixedValue { get; init; }

    /// <summary>Probability [0,1] that this path is omitted (null) in the generated document.</summary>
    [Range(0, 1)]
    public double? NullRate { get; init; }
}
