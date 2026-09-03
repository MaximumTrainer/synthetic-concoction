using System.Text.Json;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Generation;

/// <summary>
/// Generates a JSON string for a column that has <see cref="JsonPathRule"/> entries defined
/// in its <see cref="ColumnRule.JsonPaths"/> collection.
///
/// Paths use dollar-dot notation (e.g. "$.email", "$.address.city") and are mapped to
/// a nested <c>Dictionary&lt;string, object?&gt;</c> before being serialized to JSON.
/// </summary>
internal static class JsonObjectGenerator
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>
    /// Builds a JSON object string by iterating the supplied path rules and generating
    /// a value for each one using the provided dispatcher.
    /// </summary>
    public static async Task<string> GenerateAsync(
        IReadOnlyList<JsonPathRule> pathRules,
        IValueGeneratorDispatcher dispatcher,
        IRandomService random,
        string table,
        string column,
        int rowIndex,
        CancellationToken cancellationToken)
    {
        var root = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var pathRule in pathRules)
        {
            var segments = ParsePath(pathRule.Path);
            if (segments.Length == 0) continue;

            object? value;

            if (pathRule.FixedValue is not null)
            {
                value = pathRule.FixedValue;
            }
            else if (pathRule.NullRate is double nullRate
                && random.NextDouble($"json-null:{table}.{column}.{string.Join('.', segments)}.{rowIndex}") <= nullRate)
            {
                value = null;
            }
            else
            {
                var dataKind = ParseStrategy(pathRule.Strategy);
                var pathScope = string.Join('.', segments);
                var ctx = new GeneratorContext(
                    Table: table,
                    Column: $"{column}.{pathScope}",
                    DataKind: dataKind,
                    RowIndex: rowIndex,
                    Rules: null,
                    CurrentRow: new Dictionary<string, object?>(),
                    ReferencePool: new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>());

                value = await dispatcher.GenerateAsync(ctx, cancellationToken).ConfigureAwait(false);
            }

            SetNestedValue(root, segments, value);
        }

        return JsonSerializer.Serialize(root, SerializerOptions);
    }

    /// <summary>Parses "$.a.b.c" → ["a", "b", "c"]. Returns empty array on invalid input.</summary>
    internal static string[] ParsePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];

        var normalized = path.TrimStart();

        // Strip the leading "$." prefix
        if (normalized.StartsWith("$.", StringComparison.Ordinal))
            normalized = normalized[2..];
        else if (normalized.StartsWith("$", StringComparison.Ordinal))
            normalized = normalized[1..];

        if (string.IsNullOrEmpty(normalized)) return [];

        return normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Maps a strategy string to a <see cref="DataKind"/>. Defaults to <see cref="DataKind.String"/>.</summary>
    private static DataKind ParseStrategy(string? strategy)
    {
        if (string.IsNullOrWhiteSpace(strategy)) return DataKind.String;
        return Enum.TryParse<DataKind>(strategy, ignoreCase: true, out var kind) ? kind : DataKind.String;
    }

    /// <summary>
    /// Sets a value at the nested path defined by <paramref name="segments"/> within the dictionary tree.
    /// Intermediate dictionaries are created as needed.
    /// </summary>
    private static void SetNestedValue(Dictionary<string, object?> node, string[] segments, object? value)
    {
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var seg = segments[i];
            if (!node.TryGetValue(seg, out var child) || child is not Dictionary<string, object?> childDict)
            {
                childDict = new Dictionary<string, object?>(StringComparer.Ordinal);
                node[seg] = childDict;
            }

            node = childDict;
        }

        node[segments[^1]] = value;
    }
}
