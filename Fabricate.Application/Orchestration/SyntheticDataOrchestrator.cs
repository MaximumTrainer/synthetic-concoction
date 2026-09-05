using System.Security.Cryptography;
using System.Text;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Orchestration;

public sealed class SyntheticDataOrchestrator(
    ISchemaDiscoveryService schemaDiscoveryService,
    IGenerationPlanner planner,
    IRowMaterializer materializer,
    IConstraintEvaluator constraintEvaluator,
    ISensitiveFieldPolicy sensitiveFieldPolicy) : ISyntheticDataOrchestrator
{
    public Task<DatabaseSchema> DiscoverAsync(CancellationToken cancellationToken = default)
        => schemaDiscoveryService.DiscoverAsync(cancellationToken);

    public async Task<RunSummary> GenerateStreamingAsync(
        GenerationRequest request,
        IStreamingExporter exporter,
        string target,
        CancellationToken cancellationToken = default)
    {
        var streamer = materializer as IRowMaterializerStream
            ?? throw new InvalidOperationException(
                "The configured IRowMaterializer does not implement IRowMaterializerStream. " +
                "Register a materializer that implements both interfaces to use the streaming path.");

        var startedAt = DateTimeOffset.UtcNow;
        var plan = planner.BuildPlan(request.Schema);
        var keyPool = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal);
        var tableCount = 0;
        var totalRows = 0;

        // A key pool is only worth building while some table still to be generated references it. Retaining every
        // table's pool for the whole run (including leaf tables nothing references) makes a streaming run hold
        // memory proportional to the total row count for no benefit — see #82.
        var ordered = plan.OrderedTables;
        var referencedBy = BuildReferenceIndex(request.Schema);

        for (var position = 0; position < ordered.Count; position++)
        {
            var tableName = ordered[position];
            cancellationToken.ThrowIfCancellationRequested();
            var table = request.Schema.Tables.First(t => string.Equals(t.QualifiedName, tableName, StringComparison.Ordinal));

            var tableCompliance = table.Columns
                .Select(col => sensitiveFieldPolicy.Evaluate(table.QualifiedName, col, request.ComplianceProfile))
                .Where(d => d.Strategy is not SensitiveFieldStrategy.None and not SensitiveFieldStrategy.Synthesize)
                .ToDictionary(d => d.Column, d => d, StringComparer.OrdinalIgnoreCase);

            var rowCount = request.Rules?.Tables
                .FirstOrDefault(t => string.Equals(t.Table, table.QualifiedName, StringComparison.OrdinalIgnoreCase))
                ?.RowCount
                ?? (request.RequestedRowCounts.TryGetValue(table.QualifiedName, out var requested) ? requested : 10);

            await exporter.BeginTableAsync(table, target, cancellationToken).ConfigureAwait(false);

            // Only a table referenced by itself or by a table not yet generated needs its keys kept.
            var needsKeyPool = table.PrimaryKey.Count > 0
                && referencedBy.TryGetValue(table.QualifiedName, out var referrers)
                && referrers.Any(r => string.Equals(r, tableName, StringComparison.Ordinal)
                    || ordered.Skip(position + 1).Contains(r, StringComparer.Ordinal));

            var pkBuffer = needsKeyPool ? new List<IReadOnlyDictionary<string, object?>>() : null;

            await foreach (var row in streamer.StreamAsync(table, rowCount, request.Rules, keyPool, cancellationToken).ConfigureAwait(false))
            {
                IReadOnlyDictionary<string, object?> finalRow = row;
                if (tableCompliance.Count > 0)
                {
                    var mutable = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);
                    foreach (var (col, decision) in tableCompliance)
                    {
                        if (!mutable.TryGetValue(col, out var original) || original is null) continue;
                        mutable[col] = decision.Strategy switch
                        {
                            SensitiveFieldStrategy.Redact => "REDACTED",
                            SensitiveFieldStrategy.Pseudonymize => "usr_" + Convert.ToHexString(
                                System.Security.Cryptography.SHA256.HashData(
                                    System.Text.Encoding.UTF8.GetBytes("pseudo:" + original)))[..7].ToLowerInvariant(),
                            SensitiveFieldStrategy.Tokenize => "TKN-" + Convert.ToHexString(
                                System.Security.Cryptography.SHA256.HashData(
                                    System.Text.Encoding.UTF8.GetBytes("token:" + original)))[..12].ToUpperInvariant(),
                            _ => original
                        };
                    }
                    finalRow = mutable;
                }

                await exporter.WriteRowAsync(finalRow, cancellationToken).ConfigureAwait(false);
                totalRows++;

                pkBuffer?.Add(table.PrimaryKey
                    .ToDictionary(col => col, col => row.TryGetValue(col, out var v) ? v : null, StringComparer.OrdinalIgnoreCase));
            }

            await exporter.EndTableAsync(cancellationToken).ConfigureAwait(false);
            tableCount++;

            if (pkBuffer is not null) keyPool[table.QualifiedName] = pkBuffer;

            // Release the pools of tables that nothing still to come references.
            foreach (var pooled in keyPool.Keys.ToArray())
            {
                var stillNeeded = referencedBy.TryGetValue(pooled, out var users)
                    && users.Any(r => ordered.Skip(position + 1).Contains(r, StringComparer.Ordinal));
                if (!stillNeeded) keyPool.Remove(pooled);
            }
        }

        return new RunSummary(startedAt, DateTimeOffset.UtcNow, tableCount, totalRows, 0, plan.Diagnostics,
            request.Seed, request.Schema.Name, request.ComplianceProfile);
    }

    /// <summary>Maps each referenced table to the tables holding a foreign key into it (including self-references).</summary>
    private static Dictionary<string, List<string>> BuildReferenceIndex(DatabaseSchema schema)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var table in schema.Tables)
        {
            foreach (var fk in table.ForeignKeys)
            {
                if (!index.TryGetValue(fk.ReferencedTable, out var referrers))
                {
                    referrers = [];
                    index[fk.ReferencedTable] = referrers;
                }
                if (!referrers.Contains(table.QualifiedName, StringComparer.Ordinal))
                    referrers.Add(table.QualifiedName);
            }
        }
        return index;
    }

    public async Task<(GenerationResult Result, RunSummary Summary)> GenerateAsync(GenerationRequest request, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var plan = planner.BuildPlan(request.Schema);

        var keyPool = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal);
        var tableData = new List<TableData>();
        var issues = new List<ValidationIssue>();
        var compliance = new List<ComplianceDecision>();

        foreach (var tableName in plan.OrderedTables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = request.Schema.Tables.First(t => string.Equals(t.QualifiedName, tableName, StringComparison.Ordinal));

            foreach (var column in table.Columns)
            {
                compliance.Add(sensitiveFieldPolicy.Evaluate(table.QualifiedName, column, request.ComplianceProfile));
            }

            var rowCount = request.Rules?.Tables
                .FirstOrDefault(t => string.Equals(t.Table, table.QualifiedName, StringComparison.OrdinalIgnoreCase))
                ?.RowCount
                ?? (request.RequestedRowCounts.TryGetValue(table.QualifiedName, out var requested) ? requested : 10);

            var materialized = await materializer.MaterializeAsync(table, rowCount, request.Rules, keyPool, cancellationToken).ConfigureAwait(false);

            // Build a lookup of compliance decisions for this table to apply masking.
            var tableCompliance = compliance
                .Where(d => string.Equals(d.Table, table.QualifiedName, StringComparison.Ordinal))
                .ToDictionary(d => d.Column, d => d, StringComparer.OrdinalIgnoreCase);

            var maskedRows = ApplyComplianceMasking(materialized.Rows, tableCompliance);
            materialized = new TableData(materialized.Table, maskedRows);

            tableData.Add(materialized);

            var tableIssues = constraintEvaluator.Evaluate(table, materialized.Rows);
            issues.AddRange(tableIssues);

            if (table.PrimaryKey.Count > 0)
            {
                keyPool[table.QualifiedName] = materialized.Rows
                    .Select(row => (IReadOnlyDictionary<string, object?>)table.PrimaryKey
                        .ToDictionary(
                            col => col,
                            col => row.TryGetValue(col, out var val) ? val : null,
                            StringComparer.OrdinalIgnoreCase))
                    .ToArray();
            }
        }

        // Backfill self-referencing FK columns now that all rows exist.
        // Row 0 in each table gets null (tree root); subsequent rows reference a random earlier row.
        if (plan.SelfReferencingTables.Count > 0)
        {
            BackfillSelfReferences(request.Schema, plan.SelfReferencingTables, tableData, issues);
        }

        // Backfill cross-table FK columns in cyclic groups — these couldn't be resolved during
        // ordered generation. Columns must be nullable to allow a null on the first row.
        if (plan.Cycles.Count > 0)
        {
            BackfillCyclicForeignKeys(request.Schema, plan.Cycles, tableData, keyPool, issues);
        }

        var result = new GenerationResult(tableData, issues, compliance);
        var summary = new RunSummary(
            startedAt,
            DateTimeOffset.UtcNow,
            tableData.Count,
            tableData.Sum(static t => t.Rows.Count),
            issues.Count,
            plan.Diagnostics,
            request.Seed,
            request.Schema.Name,
            request.ComplianceProfile);

        return (result, summary);
    }

    private static void BackfillSelfReferences(
        DatabaseSchema schema,
        IReadOnlyList<string> selfRefTableNames,
        List<TableData> tableData,
        List<ValidationIssue> issues)
    {
        foreach (var tableName in selfRefTableNames)
        {
            var tableSchema = schema.Tables.FirstOrDefault(t => string.Equals(t.QualifiedName, tableName, StringComparison.Ordinal));
            if (tableSchema is null) continue;

            var selfRefFks = tableSchema.ForeignKeys
                .Where(fk => string.Equals(fk.ReferencedTable, tableName, StringComparison.Ordinal))
                .ToArray();
            if (selfRefFks.Length == 0) continue;

            var idx = tableData.FindIndex(t => string.Equals(t.Table, tableName, StringComparison.Ordinal));
            if (idx < 0) continue;

            var existing = tableData[idx];
            var rows = existing.Rows;

            if (rows.Count == 0) continue;

            // Collect PK values for reference
            var pkColumn = tableSchema.PrimaryKey.Count > 0 ? tableSchema.PrimaryKey[0] : null;

            var updatedRows = rows.Select((row, rowIndex) =>
            {
                var mutable = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);

                foreach (var fk in selfRefFks)
                {
                    // Row 0 is the root — nullable FK gets null; non-nullable gets self-ref to row 0 (same row if only 1 row).
                    // Rows 1+ reference a random earlier row to form a valid forest.
                    var parentRowIndex = rowIndex == 0 ? -1 : (rowIndex % rowIndex); // will resolve below

                    // Simple parent selection: row N references row (N-1) % rowCount, creating a chain.
                    // Row 0 gets null (root of tree).
                    if (rowIndex > 0 && pkColumn is not null)
                    {
                        var parentRow = rows[rowIndex - 1];
                        for (var colIdx = 0; colIdx < fk.SourceColumns.Count && colIdx < fk.ReferencedColumns.Count; colIdx++)
                        {
                            var sourceCol = fk.SourceColumns[colIdx];
                            var refCol = fk.ReferencedColumns[colIdx];
                            mutable[sourceCol] = parentRow.TryGetValue(refCol, out var refVal) ? refVal : null;
                        }
                    }
                    else
                    {
                        // Root row — ensure FK columns are null (require nullable FK)
                        foreach (var sourceCol in fk.SourceColumns)
                        {
                            var colSchema = tableSchema.Columns.FirstOrDefault(c => string.Equals(c.Name, sourceCol, StringComparison.OrdinalIgnoreCase));
                            if (colSchema?.IsNullable == true)
                            {
                                mutable[sourceCol] = null;
                            }
                            else
                            {
                                issues.Add(new ValidationIssue(tableName, sourceCol,
                                    $"Self-referencing FK '{fk.Name}' on non-nullable column '{sourceCol}' cannot be backfilled for root row. Mark column nullable."));
                            }
                        }
                    }
                }

                return (IReadOnlyDictionary<string, object?>)mutable;
            }).ToList();

            tableData[idx] = new TableData(tableName, updatedRows);
        }
    }

    /// <summary>
    /// Backfills cross-table FK columns in cyclic groups.
    /// During ordered generation, at least one table in each cycle had no parent rows yet,
    /// so those FK columns were generated as arbitrary values. This pass replaces them with
    /// valid references from the key pool now that all cycle tables have been generated.
    /// </summary>
    private static void BackfillCyclicForeignKeys(
        DatabaseSchema schema,
        IReadOnlyList<IReadOnlyList<string>> cycles,
        List<TableData> tableData,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> keyPool,
        List<ValidationIssue> issues)
    {
        foreach (var cycleGroup in cycles)
        {
            foreach (var tableName in cycleGroup)
            {
                var tableSchema = schema.Tables.FirstOrDefault(t => string.Equals(t.QualifiedName, tableName, StringComparison.Ordinal));
                if (tableSchema is null) continue;

                var cycleTableSet = new HashSet<string>(cycleGroup, StringComparer.Ordinal);

                // Only backfill FKs that point to other tables in the same cycle group.
                var cyclicFks = tableSchema.ForeignKeys
                    .Where(fk => cycleTableSet.Contains(fk.ReferencedTable)
                        && !string.Equals(fk.ReferencedTable, tableName, StringComparison.Ordinal))
                    .ToArray();

                if (cyclicFks.Length == 0) continue;
                if (!keyPool.TryGetValue(tableName, out var ownKeys) || ownKeys.Count == 0) continue;

                var idx = tableData.FindIndex(t => string.Equals(t.Table, tableName, StringComparison.Ordinal));
                if (idx < 0) continue;

                var rows = tableData[idx].Rows;
                if (rows.Count == 0) continue;

                var updatedRows = rows.Select((row, rowIndex) =>
                {
                    var mutable = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);

                    foreach (var fk in cyclicFks)
                    {
                        if (!keyPool.TryGetValue(fk.ReferencedTable, out var parentKeys) || parentKeys.Count == 0)
                        {
                            // Parent table has no rows — null out nullable FK columns
                            foreach (var sourceCol in fk.SourceColumns)
                            {
                                var colSchema = tableSchema.Columns.FirstOrDefault(c => string.Equals(c.Name, sourceCol, StringComparison.OrdinalIgnoreCase));
                                if (colSchema?.IsNullable == true)
                                    mutable[sourceCol] = null;
                                else
                                    issues.Add(new ValidationIssue(tableName, sourceCol,
                                        $"Cyclic FK '{fk.Name}' cannot be backfilled: referenced table '{fk.ReferencedTable}' has no rows and column is not nullable."));
                            }
                            continue;
                        }

                        // Row 0 gets null to break the cycle; subsequent rows reference a real parent row.
                        if (rowIndex == 0)
                        {
                            foreach (var sourceCol in fk.SourceColumns)
                            {
                                var colSchema = tableSchema.Columns.FirstOrDefault(c => string.Equals(c.Name, sourceCol, StringComparison.OrdinalIgnoreCase));
                                if (colSchema?.IsNullable == true)
                                {
                                    mutable[sourceCol] = null;
                                }
                                else
                                {
                                    // Non-nullable — point to the first parent row instead of null.
                                    var firstParent = parentKeys[0];
                                    for (var colIdx = 0; colIdx < fk.SourceColumns.Count && colIdx < fk.ReferencedColumns.Count; colIdx++)
                                    {
                                        var refCol = fk.ReferencedColumns[colIdx];
                                        mutable[fk.SourceColumns[colIdx]] = firstParent.TryGetValue(refCol, out var refVal) ? refVal : null;
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Point to a parent row using round-robin to distribute references.
                            var parentIdx = (rowIndex - 1) % parentKeys.Count;
                            var parentRow = parentKeys[parentIdx];
                            for (var colIdx = 0; colIdx < fk.SourceColumns.Count && colIdx < fk.ReferencedColumns.Count; colIdx++)
                            {
                                var sourceCol = fk.SourceColumns[colIdx];
                                var refCol = fk.ReferencedColumns[colIdx];
                                mutable[sourceCol] = parentRow.TryGetValue(refCol, out var refVal) ? refVal : null;
                            }
                        }
                    }

                    return (IReadOnlyDictionary<string, object?>)mutable;
                }).ToList();

                tableData[idx] = new TableData(tableName, updatedRows);
            }
        }
    }

    /// <summary>
    /// Applies compliance masking to generated rows based on the policy decisions for each column.
    /// None / Synthesize → value passes through unchanged.
    /// Redact  → null (for nullable columns) or "REDACTED".
    /// Pseudonymize → deterministic stable pseudonym derived from the original value's hash.
    /// Tokenize → "TKN-{hash}" reference token.
    /// </summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ApplyComplianceMasking(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyDictionary<string, ComplianceDecision> decisions)
    {
        var sensitive = decisions.Values
            .Where(d => d.Strategy is not SensitiveFieldStrategy.None and not SensitiveFieldStrategy.Synthesize)
            .ToArray();

        if (sensitive.Length == 0) return rows;

        return rows.Select(row =>
        {
            var mutable = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);

            foreach (var decision in sensitive)
            {
                if (!mutable.TryGetValue(decision.Column, out var original)) continue;
                if (original is null) continue;

                mutable[decision.Column] = decision.Strategy switch
                {
                    SensitiveFieldStrategy.Redact => "REDACTED",
                    SensitiveFieldStrategy.Pseudonymize => Pseudonymize(original.ToString() ?? string.Empty),
                    SensitiveFieldStrategy.Tokenize => Tokenize(original.ToString() ?? string.Empty),
                    _ => original
                };
            }

            return (IReadOnlyDictionary<string, object?>)mutable;
        }).ToList();
    }

    private static string Pseudonymize(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("pseudo:" + value));
        return "usr_" + Convert.ToHexString(hash)[..7].ToLowerInvariant();
    }

    private static string Tokenize(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("token:" + value));
        return "TKN-" + Convert.ToHexString(hash)[..12].ToUpperInvariant();
    }
}