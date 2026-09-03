using Fabricate.Application.Abstractions;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Generation;

public sealed class ReferentialRowMaterializer(IValueGeneratorDispatcher dispatcher, IRandomService random) : IRowMaterializer, IRowMaterializerStream
{
    public async Task<TableData> MaterializeAsync(
        TableSchema table,
        int rowCount,
        RuleConfiguration? rules,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> keyPool,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>(capacity: rowCount);

        // Track used composite keys per unique constraint to enforce uniqueness at the constraint level.
        // Key: ordered pipe-joined constraint column names. Value: set of seen composite values.
        var uniqueValueSets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            const int maxAttempts = 10;
            Dictionary<string, object?>? acceptedRow = null;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                // Pre-pick the parent row index for each FK constraint so all FK columns in the
                // same constraint resolve from the same parent row (required for composite FKs).
                var fkParentRowIndices = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var fk in table.ForeignKeys)
                {
                    if (keyPool.TryGetValue(fk.ReferencedTable, out var parentRows) && parentRows.Count > 0)
                    {
                        fkParentRowIndices[fk.Name] = random.NextInt(
                            $"fk:{table.QualifiedName}.{fk.Name}.{rowIndex}.{attempt}",
                            0,
                            parentRows.Count);
                    }
                }

                foreach (var column in table.Columns)
                {
                    object? value;

                    var fk = table.ForeignKeys.FirstOrDefault(f =>
                        f.SourceColumns.Contains(column.Name, StringComparer.OrdinalIgnoreCase));

                    if (fk is not null
                        && fkParentRowIndices.TryGetValue(fk.Name, out var parentRowIdx)
                        && keyPool.TryGetValue(fk.ReferencedTable, out var parentRows))
                    {
                        // Resolve the typed value from the correct referenced column of the chosen parent row.
                        var parentRow = parentRows[parentRowIdx];
                        var sourceMatch = fk.SourceColumns
                            .Select(static (c, i) => (c, i))
                            .FirstOrDefault(x => string.Equals(x.c, column.Name, StringComparison.OrdinalIgnoreCase));
                        var foundIdx = sourceMatch.c is not null ? sourceMatch.i : -1;
                        var referencedCol = foundIdx >= 0 && foundIdx < fk.ReferencedColumns.Count
                            ? fk.ReferencedColumns[foundIdx]
                            : column.Name;
                        value = parentRow.TryGetValue(referencedCol, out var refVal) ? refVal : null;
                    }
                    else
                    {
                        value = await GenerateColumnValueAsync(
                            table, column, HashSeed(rowIndex, attempt), rules, row, keyPool, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    row[column.Name] = value;
                }

                // Evaluate composite uniqueness at the constraint level.
                // SQL semantics: a row with NULL in any constraint column is always considered unique.
                var compositeKeyEntries = new List<(string ConstraintKey, string CompositeValue)>();
                var hasDuplicate = false;

                foreach (var constraint in table.UniqueConstraints)
                {
                    var orderedCols = constraint.Columns
                        .OrderBy(static c => c, StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    // Skip uniqueness enforcement when any column in the constraint is NULL or missing from the row.
                    // SQL semantics: NULL values are never considered equal for uniqueness purposes.
                    if (orderedCols.Any(c => !row.TryGetValue(c, out var v) || v is null))
                    {
                        continue;
                    }

                    var constraintKey = string.Join('|', orderedCols);
                    var compositeValue = string.Join('|', orderedCols
                        .Select(c => row.TryGetValue(c, out var v) ? v?.ToString() ?? string.Empty : string.Empty));

                    if (!uniqueValueSets.TryGetValue(constraintKey, out var used))
                    {
                        used = new HashSet<string>(StringComparer.Ordinal);
                        uniqueValueSets[constraintKey] = used;
                    }

                    if (used.Contains(compositeValue))
                    {
                        hasDuplicate = true;
                        break;
                    }

                    compositeKeyEntries.Add((constraintKey, compositeValue));
                }

                if (!hasDuplicate)
                {
                    // Commit all composite keys now that the row is accepted.
                    foreach (var (constraintKey, compositeValue) in compositeKeyEntries)
                    {
                        uniqueValueSets[constraintKey].Add(compositeValue);
                    }

                    acceptedRow = row;
                    break;
                }
            }

            if (acceptedRow is null)
            {
                throw new InvalidOperationException(
                    $"Unable to generate a row satisfying unique constraints for {table.QualifiedName} after {maxAttempts} attempts. " +
                    "Adjust uniqueness rules, row counts, or generator strategy for this table.");
            }

            rows.Add(acceptedRow);
        }

        return new TableData(table.QualifiedName, rows);
    }

    /// <summary>
    /// Streaming implementation of <see cref="IRowMaterializerStream"/>.
    /// Yields rows one at a time using IAsyncEnumerable — no full-table buffer.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamAsync(
        TableSchema table,
        int rowCount,
        RuleConfiguration? rules,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> keyPool,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var uniqueValueSets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            const int maxAttempts = 10;
            Dictionary<string, object?>? acceptedRow = null;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                var fkParentRowIndices = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var fk in table.ForeignKeys)
                {
                    if (keyPool.TryGetValue(fk.ReferencedTable, out var parentRows) && parentRows.Count > 0)
                    {
                        fkParentRowIndices[fk.Name] = random.NextInt(
                            $"fk:{table.QualifiedName}.{fk.Name}.{rowIndex}.{attempt}", 0, parentRows.Count);
                    }
                }

                foreach (var column in table.Columns)
                {
                    object? value;
                    var fk = table.ForeignKeys.FirstOrDefault(f =>
                        f.SourceColumns.Contains(column.Name, StringComparer.OrdinalIgnoreCase));

                    if (fk is not null
                        && fkParentRowIndices.TryGetValue(fk.Name, out var parentRowIdx)
                        && keyPool.TryGetValue(fk.ReferencedTable, out var parentRows))
                    {
                        var parentRow = parentRows[parentRowIdx];
                        var sourceMatch = fk.SourceColumns
                            .Select(static (c, i) => (c, i))
                            .FirstOrDefault(x => string.Equals(x.c, column.Name, StringComparison.OrdinalIgnoreCase));
                        var foundIdx = sourceMatch.c is not null ? sourceMatch.i : -1;
                        var referencedCol = foundIdx >= 0 && foundIdx < fk.ReferencedColumns.Count
                            ? fk.ReferencedColumns[foundIdx] : column.Name;
                        value = parentRow.TryGetValue(referencedCol, out var refVal) ? refVal : null;
                    }
                    else
                    {
                        value = await GenerateColumnValueAsync(
                            table, column, HashSeed(rowIndex, attempt), rules, row, keyPool, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    row[column.Name] = value;
                }

                var compositeKeyEntries = new List<(string ConstraintKey, string CompositeValue)>();
                var hasDuplicate = false;

                foreach (var constraint in table.UniqueConstraints)
                {
                    var orderedCols = constraint.Columns
                        .OrderBy(static c => c, StringComparer.OrdinalIgnoreCase).ToArray();

                    if (orderedCols.Any(c => !row.TryGetValue(c, out var v) || v is null))
                        continue;

                    var constraintKey = string.Join('|', orderedCols);
                    var compositeValue = string.Join('|', orderedCols
                        .Select(c => row.TryGetValue(c, out var v) ? v?.ToString() ?? string.Empty : string.Empty));

                    if (!uniqueValueSets.TryGetValue(constraintKey, out var used))
                    {
                        used = new HashSet<string>(StringComparer.Ordinal);
                        uniqueValueSets[constraintKey] = used;
                    }

                    if (used.Contains(compositeValue)) { hasDuplicate = true; break; }
                    compositeKeyEntries.Add((constraintKey, compositeValue));
                }

                if (!hasDuplicate)
                {
                    foreach (var (constraintKey, compositeValue) in compositeKeyEntries)
                        uniqueValueSets[constraintKey].Add(compositeValue);
                    acceptedRow = row;
                    break;
                }
            }

            if (acceptedRow is null)
            {
                throw new InvalidOperationException(
                    $"Unable to generate a row satisfying unique constraints for {table.QualifiedName} after {maxAttempts} attempts.");
            }

            yield return acceptedRow;
        }
    }

    private async Task<object?> GenerateColumnValueAsync(
        TableSchema table,
        ColumnSchema column,
        int rowIndex,
        RuleConfiguration? rules,
        Dictionary<string, object?> row,
        IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> keyPool,
        CancellationToken cancellationToken)
    {
        var columnRule = rules?.Tables
            .FirstOrDefault(t => string.Equals(t.Table, table.QualifiedName, StringComparison.OrdinalIgnoreCase))
            ?.Columns.FirstOrDefault(c => string.Equals(c.Column, column.Name, StringComparison.OrdinalIgnoreCase));

        if (columnRule?.FixedValue is not null)
        {
            return columnRule.FixedValue;
        }

        if (column.IsNullable && columnRule?.NullRate is double nullRate
            && random.NextDouble($"null:{table.QualifiedName}.{column.Name}.{rowIndex}") <= nullRate)
        {
            return null;
        }

        // Distribution-based weighted value sampling: pick from an explicit value→weight map.
        if (columnRule?.Distribution is { Count: > 0 } distribution)
        {
            return SampleFromDistribution(distribution, $"dist:{table.QualifiedName}.{column.Name}.{rowIndex}");
        }

        // JSON path-level strategy: build a nested JSON object instead of dispatching a scalar generator.
        if (columnRule?.JsonPaths is { Count: > 0 } jsonPaths)
        {
            return await JsonObjectGenerator.GenerateAsync(
                jsonPaths, dispatcher, random,
                table.QualifiedName, column.Name, rowIndex,
                cancellationToken).ConfigureAwait(false);
        }

        // Allow ColumnRule.Strategy to override the column's inferred DataKind (e.g. "Email" on a String column).
        var dataKind = column.DataKind;
        if (!string.IsNullOrWhiteSpace(columnRule?.Strategy)
            && Enum.TryParse<DataKind>(columnRule.Strategy, ignoreCase: true, out var overrideKind))
        {
            dataKind = overrideKind;
        }

        var context = new GeneratorContext(
            table.QualifiedName, column.Name, dataKind, rowIndex, rules, row, keyPool);
        var candidate = await dispatcher.GenerateAsync(context, cancellationToken).ConfigureAwait(false);

        if (candidate is string text && column.MaxLength is int maxLength && text.Length > maxLength)
        {
            candidate = text[..maxLength];
        }

        if (column.AllowedValues is { Count: > 0 } allowedValues && candidate is not null)
        {
            candidate = allowedValues[
                random.NextInt($"enum:{table.QualifiedName}.{column.Name}.{rowIndex}", 0, allowedValues.Count)];
        }

        return candidate;
    }

    /// <summary>
    /// Combines rowIndex and attempt into a single bounded integer seed to avoid integer overflow
    /// for large datasets and keep values deterministic.
    /// </summary>
    private static int HashSeed(int rowIndex, int attempt)
    {
        var hash = HashCode.Combine(rowIndex, attempt);
        return hash < 0 ? -(hash + 1) : hash; // ensure non-negative
    }

    /// <summary>
    /// Samples a value from a weighted distribution using cumulative probability.
    /// Weights do not need to sum to 1 — they are normalised internally.
    /// </summary>
    private object? SampleFromDistribution(Dictionary<string, double> distribution, string scope)
    {
        var totalWeight = 0.0;
        foreach (var weight in distribution.Values)
            totalWeight += weight > 0 ? weight : 0;

        if (totalWeight <= 0)
            return distribution.Keys.FirstOrDefault();

        var sample = random.NextDouble(scope) * totalWeight;
        var cumulative = 0.0;
        foreach (var (value, weight) in distribution)
        {
            if (weight <= 0) continue;
            cumulative += weight;
            if (sample <= cumulative)
                return value;
        }

        return distribution.Keys.Last();
    }
}
