using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Schema;

/// <summary>
/// Shared utility for inferring <see cref="FieldDescriptor"/> collections from sampled document data.
/// Used by all NoSQL adapter implementations to union field names and types across a sample set.
/// </summary>
internal static class FieldInferenceHelper
{
    /// <summary>
    /// Working accumulator for field inference — maps field name to mutable field state.
    /// Create one per collection, call <see cref="FieldAccumulator.Observe"/> for each field
    /// in each sampled document, then call <see cref="FieldAccumulator.Build"/> to get descriptors.
    /// </summary>
    public sealed class FieldAccumulator
    {
        private readonly Dictionary<string, FieldState> _fields = new(StringComparer.Ordinal);

        /// <summary>
        /// Records one observation of a field with the given type.
        /// If the field was observed before with a different type, the type is widened to
        /// <see cref="DocumentFieldType.Unknown"/>. Null observations mark the field nullable
        /// without changing the inferred type.
        /// </summary>
        public void Observe(string name, DocumentFieldType type, bool isNull, Action<FieldAccumulator>? observeNested = null)
        {
            if (!_fields.TryGetValue(name, out var state))
            {
                state = new FieldState(isNull ? DocumentFieldType.Null : type);
                _fields[name] = state;
            }

            if (isNull)
            {
                state.IsNullable = true;
            }
            else
            {
                state.Widen(type);
                if (observeNested is not null && type == DocumentFieldType.Object)
                {
                    state.Nested ??= new FieldAccumulator();
                    observeNested(state.Nested);
                }
            }
        }

        /// <summary>
        /// Builds an ordered, immutable list of <see cref="FieldDescriptor"/> from all observations.
        /// </summary>
        public IReadOnlyList<FieldDescriptor> Build()
        {
            return _fields
                .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new FieldDescriptor(
                    kv.Key,
                    kv.Value.Type,
                    kv.Value.IsNullable,
                    kv.Value.Nested?.Build()))
                .ToArray();
        }
    }

    private sealed class FieldState(DocumentFieldType initialType)
    {
        public DocumentFieldType Type { get; private set; } = initialType;
        public bool IsNullable { get; set; }
        public FieldAccumulator? Nested { get; set; }

        public void Widen(DocumentFieldType observed)
        {
            if (Type == observed) return;
            if (Type == DocumentFieldType.Unknown) return;
            // First non-null observation after nulls: upgrade from Null placeholder to real type
            if (Type == DocumentFieldType.Null)
            {
                Type = observed;
                return;
            }
            // Conflicting types → Unknown
            Type = DocumentFieldType.Unknown;
        }
    }
}
