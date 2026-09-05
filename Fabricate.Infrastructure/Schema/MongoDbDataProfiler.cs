using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Fabricate.Infrastructure.Schema;

/// <summary>
/// Aggregate-only profiler for MongoDB (#71).
///
/// <para>
/// The document count comes from the server's own estimate; the per-field statistics come from a bounded sample.
/// Nothing a document contains survives past <see cref="NoSqlProfileAccumulator"/>, which folds each value into
/// counters as it is seen.
/// </para>
/// </summary>
public sealed class MongoDbDataProfiler : INoSqlDataProfiler
{
    /// <summary>Documents sampled per collection. Matches the discoverer, so the two agree about what they saw.</summary>
    private const int SampleSize = 200;

    public string ProviderName => "mongodb";

    public async Task<NoSqlProfileSnapshot> ProfileAsync(
        IReadOnlyList<CollectionMetadata> collections,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collections);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = Environment.GetEnvironmentVariable("MONGODB_CONNECTION_STRING")
                ?? throw new InvalidOperationException(
                    "MongoDB connection string must be provided or MONGODB_CONNECTION_STRING must be set.");
        }

        var client = new MongoClient(connectionString);
        var profiles = new List<CollectionProfile>();

        foreach (var metadata in collections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var database = client.GetDatabase(metadata.DatabaseName);
            var collection = database.GetCollection<BsonDocument>(metadata.CollectionName);
            var accumulator = new NoSqlProfileAccumulator(metadata.QualifiedName);

            // The true count, not the sample size: a presence ratio measured against 200 of a million documents
            // would be a statement about the sample rather than about the collection.
            var total = await collection.EstimatedDocumentCountAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            var sample = await collection.Find(FilterDefinition<BsonDocument>.Empty)
                .Limit(SampleSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            var known = metadata.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var document in sample)
            {
                accumulator.BeginDocument();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                Fold(accumulator, document, prefix: null, seen);

                foreach (var missing in known.Except(seen, StringComparer.Ordinal))
                {
                    accumulator.MarkAbsent(missing);
                }
            }

            var profile = accumulator.Build();
            profiles.Add(profile with { DocumentCount = Math.Max(total, profile.DocumentCount) });
        }

        return new NoSqlProfileSnapshot(
            Guid.NewGuid(), ProviderName,
            collections.FirstOrDefault()?.DatabaseName ?? string.Empty,
            DateTimeOffset.UtcNow, profiles);
    }

    /// <summary>
    /// Walks a document, folding scalars into the accumulator. Nested objects are walked; arrays are recorded by
    /// type and length only, because the interesting statistic about an array field is that it is one.
    /// </summary>
    private static void Fold(NoSqlProfileAccumulator accumulator, BsonDocument document, string? prefix, HashSet<string> seen)
    {
        foreach (var element in document.Elements)
        {
            // Skipped for the same reason the discoverer skips it: an _id says nothing about the customer's
            // data, and profiling it would put a set of real identifiers in the snapshot.
            if (prefix is null && element.Name == "_id") continue;

            var path = prefix is null ? element.Name : $"{prefix}.{element.Name}";
            seen.Add(path);

            switch (element.Value.BsonType)
            {
                case BsonType.Document:
                    accumulator.Observe(path, DocumentFieldType.Object, value: null);
                    Fold(accumulator, element.Value.AsBsonDocument, path, seen);
                    break;

                case BsonType.Array:
                    accumulator.Observe(path, DocumentFieldType.Array, element.Value.AsBsonArray.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;

                case BsonType.Null or BsonType.Undefined:
                    accumulator.Observe(path, DocumentFieldType.Null, value: null);
                    break;

                case BsonType.String:
                    accumulator.Observe(path, DocumentFieldType.String, element.Value.AsString);
                    break;

                case BsonType.Boolean:
                    accumulator.Observe(path, DocumentFieldType.Boolean, element.Value.AsBoolean ? "true" : "false");
                    break;

                case BsonType.Int32 or BsonType.Int64 or BsonType.Double or BsonType.Decimal128:
                    var number = element.Value.ToDouble();
                    accumulator.Observe(path, DocumentFieldType.Number,
                        number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        NoSqlProfileAccumulator.Sortable(number));
                    break;

                case BsonType.DateTime:
                    var moment = element.Value.ToUniversalTime();
                    accumulator.Observe(path, DocumentFieldType.Date, moment.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                    break;

                case BsonType.ObjectId:
                    accumulator.Observe(path, DocumentFieldType.ObjectId, element.Value.AsObjectId.ToString());
                    break;

                case BsonType.Binary:
                    // The length, never the bytes.
                    accumulator.Observe(path, DocumentFieldType.Binary, element.Value.AsBsonBinaryData.Bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;

                default:
                    accumulator.Observe(path, DocumentFieldType.Unknown, element.Value.BsonType.ToString());
                    break;
            }
        }
    }
}
