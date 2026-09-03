using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Planning;

public sealed class DependencyGraphPlanner : IGenerationPlanner
{
    public GenerationPlan BuildPlan(DatabaseSchema schema)
    {
        var tableNames = schema.Tables.Select(static t => t.QualifiedName).OrderBy(static n => n, StringComparer.Ordinal).ToArray();
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var selfReferences = new List<string>();

        foreach (var table in tableNames)
        {
            edges[table] = [];
            inDegree[table] = 0;
        }

        foreach (var table in schema.Tables)
        {
            foreach (var fk in table.ForeignKeys)
            {
                // Skip self-references — they are handled via nullable FK backfill, not ordering.
                if (string.Equals(fk.ReferencedTable, table.QualifiedName, StringComparison.Ordinal))
                {
                    selfReferences.Add(table.QualifiedName);
                    continue;
                }

                if (edges.TryGetValue(fk.ReferencedTable, out var children) && children.Add(table.QualifiedName))
                {
                    inDegree[table.QualifiedName]++;
                }
            }
        }

        var queue = new Queue<string>(inDegree.Where(static x => x.Value == 0).Select(static x => x.Key).OrderBy(static x => x, StringComparer.Ordinal));
        var ordered = new List<string>(tableNames.Length);

        while (queue.TryDequeue(out var node))
        {
            ordered.Add(node);

            foreach (var child in edges[node].OrderBy(static x => x, StringComparer.Ordinal))
            {
                inDegree[child]--;
                if (inDegree[child] == 0)
                {
                    queue.Enqueue(child);
                }
            }
        }

        var diagnostics = new List<string>();

        if (selfReferences.Count > 0)
        {
            var distinct = selfReferences.Distinct(StringComparer.Ordinal).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
            diagnostics.Add($"Self-referencing tables detected (FK backfill deferred): {string.Join(", ", distinct)}");
        }

        if (ordered.Count == tableNames.Length)
        {
            return new GenerationPlan(ordered, [], diagnostics, selfReferences.Distinct(StringComparer.Ordinal).Order().ToArray());
        }

        // Cycle detected: append cycle tables in deterministic order so generation still proceeds
        // (cyclic FK columns must be nullable to allow deferred backfill).
        var cycleTables = inDegree.Where(static x => x.Value > 0).Select(static x => x.Key).OrderBy(static x => x, StringComparer.Ordinal).ToArray();
        diagnostics.Add($"Cycle detected among tables: {string.Join(", ", cycleTables)}");

        return new GenerationPlan(
            ordered.Concat(cycleTables).ToArray(),
            [cycleTables],
            diagnostics,
            selfReferences.Distinct(StringComparer.Ordinal).Order().ToArray());
    }
}
