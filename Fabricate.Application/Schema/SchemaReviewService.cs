using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Schema;

/// <summary>Reviews a discovered schema, returning diagnostics for unsupported types and structural anomalies.</summary>
public sealed class SchemaReviewService : ISchemaReviewService
{
    public SchemaReviewReport Review(DatabaseSchema schema)
    {
        var diagnostics = new List<SchemaReviewDiagnostic>();
        var unknownTypes = SqlTypeMapper.GetUnknownTypeDiagnostics();

        foreach (var table in schema.Tables)
        {
            if (table.PrimaryKey.Count == 0)
            {
                diagnostics.Add(new SchemaReviewDiagnostic("warning", table.QualifiedName, "Table has no primary key. Referential integrity may be limited."));
            }

            foreach (var column in table.Columns)
            {
                if (column.DataKind == Domain.Enums.DataKind.Unknown)
                {
                    diagnostics.Add(new SchemaReviewDiagnostic("warning", $"{table.QualifiedName}.{column.Name}", $"Column has unsupported SQL type '{column.SqlType}'. Values will be generated as null."));
                }
            }

            foreach (var fk in table.ForeignKeys)
            {
                var referencedExists = schema.Tables.Any(t => string.Equals(t.QualifiedName, fk.ReferencedTable, StringComparison.Ordinal));
                if (!referencedExists)
                {
                    diagnostics.Add(new SchemaReviewDiagnostic("error", table.QualifiedName, $"Foreign key '{fk.Name}' references unknown table '{fk.ReferencedTable}'."));
                }
            }
        }

        return new SchemaReviewReport(schema, diagnostics, unknownTypes);
    }
}
