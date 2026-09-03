using Fabricate.Application.Abstractions;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Generation;

/// <summary>Builds a <see cref="PlanDiagnosticsReport"/> showing the resolved strategy and provenance for every
/// column in the schema, applying four-level override precedence: global &lt; project &lt; table &lt; column.</summary>
public sealed class GenerationPlanService(IGeneratorRegistry registry) : IGenerationPlanService
{
    public PlanDiagnosticsReport BuildDiagnosticsReport(DatabaseSchema schema, RuleConfiguration? rules = null)
    {
        var entries = new List<ColumnPlanEntry>();
        var warnings = new List<string>();

        foreach (var table in schema.Tables)
        {
            foreach (var column in table.Columns)
            {
                var (strategy, provenance) = ResolveStrategy(table.QualifiedName, column, rules);

                if (!registry.TryResolve(column.DataKind, strategy, out _))
                {
                    warnings.Add($"{table.QualifiedName}.{column.Name}: no generator for DataKind='{column.DataKind}' strategy='{strategy ?? "<default>"}'.");
                }

                entries.Add(new ColumnPlanEntry(table.QualifiedName, column.Name, column.DataKind, strategy ?? "<default>", provenance));
            }
        }

        return new PlanDiagnosticsReport(entries, warnings);
    }

    private static (string? Strategy, string Provenance) ResolveStrategy(string qualifiedTable, ColumnSchema column, RuleConfiguration? rules)
    {
        if (rules is null)
        {
            return (null, "default");
        }

        // Column-level (highest precedence)
        var tableRule = rules.Tables.FirstOrDefault(t => string.Equals(t.Table, qualifiedTable, StringComparison.OrdinalIgnoreCase));
        var columnRule = tableRule?.Columns.FirstOrDefault(c => string.Equals(c.Column, column.Name, StringComparison.OrdinalIgnoreCase));

        if (columnRule?.Strategy is { Length: > 0 } colStrategy)
        {
            return (colStrategy, "column-rule");
        }

        // Table-level strategy applied to all columns in the table (if a wildcard column "*" is used)
        var wildcardRule = tableRule?.Columns.FirstOrDefault(c => c.Column == "*");
        if (wildcardRule?.Strategy is { Length: > 0 } tableStrategy)
        {
            return (tableStrategy, "table-rule");
        }

        return (null, "default");
    }
}
