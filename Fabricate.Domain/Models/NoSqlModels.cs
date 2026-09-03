namespace Fabricate.Domain.Models;

/// <summary>
/// Represents the field type for a document/NoSQL field.
/// Uses a superset of SQL types to accommodate document-store specifics such as
/// nested objects, arrays, and mixed-type fields.
/// </summary>
public enum DocumentFieldType
{
    String,
    Number,
    Boolean,
    Object,
    Array,
    Null,
    Binary,
    Date,
    ObjectId,
    Unknown
}

/// <summary>
/// Describes a single field within a document collection, including any nested fields.
/// </summary>
public sealed record FieldDescriptor(
    string Name,
    DocumentFieldType FieldType,
    bool IsNullable,
    IReadOnlyList<FieldDescriptor>? NestedFields = null);

/// <summary>
/// Describes a partition key or shard key used by the collection's storage engine.
/// </summary>
public sealed record PartitionKeyDescriptor(
    string FieldPath,
    string? KeyType = null);

/// <summary>
/// Describes an index on a document collection.
/// </summary>
public sealed record CollectionIndexDescriptor(
    string Name,
    IReadOnlyList<string> FieldPaths,
    bool IsUnique,
    bool IsSparse = false);

/// <summary>
/// Canonical metadata model for a document/NoSQL collection.
/// Analogous to <see cref="TableSchema"/> for relational databases.
/// </summary>
public sealed record CollectionMetadata(
    string DatabaseName,
    string CollectionName,
    IReadOnlyList<FieldDescriptor> Fields,
    PartitionKeyDescriptor? PartitionKey,
    IReadOnlyList<CollectionIndexDescriptor> Indexes,
    IReadOnlyDictionary<string, string>? ProviderProperties = null)
{
    /// <summary>Qualified name in the form "database.collection".</summary>
    public string QualifiedName => $"{DatabaseName}.{CollectionName}";
}

/// <summary>
/// Profile statistics for a document collection.
/// Mirrors <see cref="TableProfile"/> for NoSQL providers.
/// Only aggregate statistics — no raw document values are captured.
/// </summary>
public sealed record CollectionProfile(
    string QualifiedName,
    long DocumentCount,
    IReadOnlyList<FieldProfile> FieldProfiles);

/// <summary>
/// Aggregate statistics for a single field within a collection.
/// </summary>
public sealed record FieldProfile(
    string FieldPath,
    DocumentFieldType InferredType,
    long NonNullCount,
    long NullCount,
    long ApproximateDistinctValues,
    string? MinValue,
    string? MaxValue);

/// <summary>
/// Result of profiling all collections in a NoSQL database/account.
/// </summary>
public sealed record NoSqlProfileSnapshot(
    Guid Id,
    string ProviderName,
    string DatabaseName,
    DateTimeOffset CapturedAt,
    IReadOnlyList<CollectionProfile> Collections);
