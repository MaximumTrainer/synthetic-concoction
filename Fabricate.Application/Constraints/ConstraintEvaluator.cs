using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Constraints;

public sealed class ConstraintEvaluator : IConstraintEvaluator
{
    public IReadOnlyList<ValidationIssue> Evaluate(TableSchema table, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var issues = new List<ValidationIssue>();

        foreach (var column in table.Columns)
        {
            var values = rows.Select(r => r.TryGetValue(column.Name, out var value) ? value : null).ToArray();

            if (!column.IsNullable)
            {
                issues.AddRange(values
                    .Select((value, index) => (value, index))
                    .Where(static tuple => tuple.value is null)
                    .Select(tuple => new ValidationIssue(table.QualifiedName, column.Name, $"Null value at row {tuple.index} for non-nullable column.")));
            }

            if (column.MaxLength is int maxLength)
            {
                issues.AddRange(values
                    .Where(static value => value is string)
                    .Cast<string>()
                    .Where(value => value.Length > maxLength)
                    .Select(_ => new ValidationIssue(table.QualifiedName, column.Name, $"String exceeds max length {maxLength}.")));
            }

            if (column.AllowedValues is { Count: > 0 } allowedValues)
            {
                var set = allowedValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
                issues.AddRange(values
                    .Where(static value => value is not null)
                    .Where(value => !set.Contains(value!.ToString()!))
                    .Select(value => new ValidationIssue(table.QualifiedName, column.Name, $"Value '{value}' is outside allowed enum set.")));
            }
        }

        foreach (var uniqueConstraint in table.UniqueConstraints)
        {
            var duplicates = rows
                .Select((row, index) =>
                {
                    var columnValues = uniqueConstraint.Columns
                        .Select(column => row.TryGetValue(column, out var value) ? value : null)
                        .ToArray();

                    // SQL semantics: NULL values are never equal to each other, so rows with any NULL
                    // in a unique constraint column cannot violate uniqueness.
                    if (columnValues.Any(static v => v is null))
                    {
                        return (key: (string?)null, index);
                    }

                    var key = string.Join('|', columnValues.Select(static v => v?.ToString() ?? string.Empty));
                    return (key, index);
                })
                .Where(static x => x.key is not null)
                .GroupBy(static x => x.key, StringComparer.Ordinal)
                .Where(static g => g.Count() > 1)
                .SelectMany(group => group.Select(item => new ValidationIssue(table.QualifiedName, string.Join(',', uniqueConstraint.Columns), $"Duplicate unique key '{group.Key}' at row {item.index}.")));

            issues.AddRange(duplicates);
        }

        return issues;
    }
}
