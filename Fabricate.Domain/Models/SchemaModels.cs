using Fabricate.Domain.Enums;

namespace Fabricate.Domain.Models;

public sealed record DatabaseSchema(string Name, IReadOnlyList<TableSchema> Tables)
{
    public IEnumerable<TableSchema> OrderedTables => Tables.OrderBy(static t => t.QualifiedName, StringComparer.Ordinal);
}

public sealed record TableSchema(
    string Schema,
    string Name,
    IReadOnlyList<ColumnSchema> Columns,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<ForeignKeySchema> ForeignKeys,
    IReadOnlyList<UniqueConstraintSchema> UniqueConstraints,
    IReadOnlyList<IndexSchema> Indexes)
{
    public string QualifiedName => $"{Schema}.{Name}";
}

public sealed record ColumnSchema(
    string Name,
    string SqlType,
    DataKind DataKind,
    bool IsNullable,
    bool IsPrimaryKey,
    bool IsUnique,
    int? MaxLength,
    int? Precision,
    int? Scale,
    string? DefaultExpression,
    IReadOnlyList<string>? AllowedValues = null);

public sealed record ForeignKeySchema(
    string Name,
    string SourceTable,
    IReadOnlyList<string> SourceColumns,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns);

public sealed record UniqueConstraintSchema(string Name, IReadOnlyList<string> Columns);

public sealed record IndexSchema(string Name, IReadOnlyList<string> Columns, bool IsUnique);
