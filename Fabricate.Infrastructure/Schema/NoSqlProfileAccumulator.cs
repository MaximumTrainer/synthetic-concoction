using System.Globalization;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Schema;

/// <summary>
/// Builds a <see cref="CollectionProfile"/> from sampled documents while holding no document (#71).
///
/// <para>
/// The whole point of a profiler is that it never becomes a copy of the data. Values are folded into counters
/// and into a bounded distinct set the moment they are seen, and the only values that survive into the snapshot
/// are the minimum and the maximum — which the profile model asks for by name, and which callers already treat
/// as aggregate statistics rather than sampled rows (#83).
/// </para>
/// </summary>
internal sealed class NoSqlProfileAccumulator(string qualifiedName)
{
    /// <summary>
    /// Distinct values are counted exactly up to this many and estimated as "at least this" beyond it. An exact
    /// count would mean holding every distinct value, which for a high-cardinality field is the data itself.
    /// </summary>
    private const int DistinctCap = 1000;

    private readonly Dictionary<string, FieldAccumulator> _fields = new(StringComparer.Ordinal);

    public long DocumentCount { get; private set; }

    public void BeginDocument() => DocumentCount++;

    /// <summary>
    /// Folds one field of one document in. <paramref name="comparable"/> is the value in a form that orders
    /// correctly as text — an ISO timestamp, a zero-padded number — or null when the type has no useful order.
    ///
    /// <para>
    /// For a <see cref="DocumentFieldType.String"/> field the reported minimum and maximum are the shortest and
    /// longest <em>lengths</em>, not the values. A string min/max is a verbatim customer value, and on a field
    /// with few distinct values it is the field's content: a free-text note column with one entry would report
    /// that entry as both. The length range carries the shape information without the content.
    /// </para>
    /// </summary>
    public void Observe(string fieldPath, DocumentFieldType type, string? value, string? comparable = null)
    {
        if (!_fields.TryGetValue(fieldPath, out var field))
        {
            field = new FieldAccumulator(type);
            _fields[fieldPath] = field;
        }

        field.Observe(type, value, comparable);
    }

    /// <summary>Records that a document did not carry the field at all, which is what presence ratio measures.</summary>
    public void MarkAbsent(string fieldPath)
    {
        if (_fields.TryGetValue(fieldPath, out var field)) field.Absent++;
    }

    public CollectionProfile Build()
    {
        // Every document that did not carry a field counts as a null for it, which is how a document store's
        // "field present in 60% of documents" becomes the same presence ratio a relational profile reports.
        var profiles = _fields
            .Select(kv => kv.Value.Build(kv.Key, DocumentCount))
            .OrderBy(p => p.FieldPath, StringComparer.Ordinal)
            .ToArray();

        return new CollectionProfile(qualifiedName, DocumentCount, profiles);
    }

    private sealed class FieldAccumulator(DocumentFieldType initialType)
    {
        private readonly HashSet<string> _distinct = new(StringComparer.Ordinal);
        private DocumentFieldType _type = initialType;
        private bool _mixed;
        private string? _min;
        private string? _max;

        public long NonNull { get; private set; }
        public long Nulls { get; private set; }
        public long Absent { get; set; }
        private bool DistinctOverflowed { get; set; }

        public void Observe(DocumentFieldType type, string? value, string? comparable)
        {
            if (type != _type && type != DocumentFieldType.Null)
            {
                // A field with more than one type across documents is normal in a document store; reporting the
                // first type seen would be a lie, so it degrades to Unknown rather than picking a winner.
                if (_type == DocumentFieldType.Null) _type = type;
                else if (_type != type) _mixed = true;
            }

            if (value is null)
            {
                Nulls++;
                return;
            }

            NonNull++;

            // Hashed, not stored: only the *count* of distinct values is ever reported, so the accumulator has no
            // reason to hold the values themselves even transiently.
            var fingerprint = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..16];

            if (_distinct.Count < DistinctCap) _distinct.Add(fingerprint);
            else if (!_distinct.Contains(fingerprint)) DistinctOverflowed = true;

            // A string's min/max is its content, so the length range stands in for it.
            var order = type == DocumentFieldType.String
                ? Sortable(value.Length)
                : comparable ?? value;

            if (_min is null || string.CompareOrdinal(order, _min) < 0) _min = order;
            if (_max is null || string.CompareOrdinal(order, _max) > 0) _max = order;
        }

        public FieldProfile Build(string fieldPath, long documentCount)
        {
            var nulls = Nulls + Math.Max(0, documentCount - NonNull - Nulls);

            return new FieldProfile(
                fieldPath,
                _mixed ? DocumentFieldType.Unknown : _type,
                NonNull,
                nulls,
                DistinctOverflowed ? DistinctCap : _distinct.Count,
                _min,
                _max);
        }
    }

    /// <summary>Formats a number so ordinal comparison matches numeric order, for min/max on numeric fields.</summary>
    internal static string Sortable(double value)
        => value.ToString("+0000000000000000.000000;-0000000000000000.000000;+0000000000000000.000000", CultureInfo.InvariantCulture);
}
