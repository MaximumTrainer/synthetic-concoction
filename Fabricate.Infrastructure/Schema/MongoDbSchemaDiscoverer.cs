using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Fabricate.Infrastructure.Schema;

/// <summary>
/// MongoDB schema discovery adapter.
/// Samples up to 200 documents per collection to infer field types from BSON values.
/// Reads index metadata via the MongoDB index manager.
/// </summary>
/// <remarks>
/// Authentication: provide a MongoDB connection string in <c>connectionString</c>
/// (e.g. <c>mongodb://host:27017</c>, Atlas SRV URI, or connection string with credentials),
/// or set the <c>MONGODB_CONNECTION_STRING</c> environment variable.
/// </remarks>
public sealed class MongoDbSchemaDiscoverer : INoSqlSchemaDiscoverer
{
    public string ProviderName => "mongodb";

    public async Task<IReadOnlyList<CollectionMetadata>> DiscoverCollectionsAsync(
        string connectionString,
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = Environment.GetEnvironmentVariable("MONGODB_CONNECTION_STRING")
                ?? throw new InvalidOperationException(
                    "MongoDB connection string must be provided or MONGODB_CONNECTION_STRING environment variable must be set.");

        var client = new MongoClient(connectionString);
        IMongoDatabase database = client.GetDatabase(databaseName);

        var cursor = await database.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        var collectionNames = await cursor.ToListAsync(cancellationToken);

        var results = new List<CollectionMetadata>();
        foreach (var name in collectionNames)
        {
            IMongoCollection<BsonDocument> collection = database.GetCollection<BsonDocument>(name);

            var acc = new FieldInferenceHelper.FieldAccumulator();
            var docs = await collection.Find(FilterDefinition<BsonDocument>.Empty)
                .Limit(200)
                .ToListAsync(cancellationToken);

            foreach (BsonDocument doc in docs)
                InferFieldsFromBson(acc, doc);

            var indexes = await DiscoverIndexesAsync(collection, cancellationToken);

            results.Add(new CollectionMetadata(
                databaseName,
                name,
                acc.Build(),
                null,
                indexes));
        }

        return results;
    }

    private static void InferFieldsFromBson(FieldInferenceHelper.FieldAccumulator acc, BsonDocument doc)
    {
        foreach (BsonElement element in doc.Elements)
        {
            // Skip MongoDB internal fields
            if (element.Name == "_id")
            {
                acc.Observe(element.Name, DocumentFieldType.ObjectId, false);
                continue;
            }

            bool isNull = element.Value.BsonType is BsonType.Null or BsonType.Undefined;
            DocumentFieldType type = element.Value.BsonType switch
            {
                BsonType.String => DocumentFieldType.String,
                BsonType.Double => DocumentFieldType.Number,
                BsonType.Int32 => DocumentFieldType.Number,
                BsonType.Int64 => DocumentFieldType.Number,
                BsonType.Decimal128 => DocumentFieldType.Number,
                BsonType.Boolean => DocumentFieldType.Boolean,
                BsonType.Document => DocumentFieldType.Object,
                BsonType.Array => DocumentFieldType.Array,
                BsonType.Null => DocumentFieldType.Null,
                BsonType.Undefined => DocumentFieldType.Null,
                BsonType.ObjectId => DocumentFieldType.ObjectId,
                BsonType.Binary => DocumentFieldType.Binary,
                BsonType.DateTime => DocumentFieldType.Date,
                BsonType.Timestamp => DocumentFieldType.Date,
                _ => DocumentFieldType.Unknown
            };

            if (type == DocumentFieldType.Object)
                acc.Observe(element.Name, type, isNull, nested => InferFieldsFromBson(nested, element.Value.AsBsonDocument));
            else
                acc.Observe(element.Name, type, isNull);
        }
    }

    private static async Task<IReadOnlyList<CollectionIndexDescriptor>> DiscoverIndexesAsync(
        IMongoCollection<BsonDocument> collection,
        CancellationToken cancellationToken)
    {
        var indexCursor = await collection.Indexes.ListAsync(cancellationToken: cancellationToken);
        var indexDocs = await indexCursor.ToListAsync(cancellationToken);

        var indexes = new List<CollectionIndexDescriptor>();
        foreach (BsonDocument indexDoc in indexDocs)
        {
            string indexName = indexDoc.GetValue("name", "unknown").AsString;
            bool unique = indexDoc.TryGetValue("unique", out var uniqueVal) && uniqueVal.AsBoolean;
            bool sparse = indexDoc.TryGetValue("sparse", out var sparseVal) && sparseVal.AsBoolean;

            var fieldPaths = new List<string>();
            if (indexDoc.TryGetValue("key", out var keyVal) && keyVal.BsonType == BsonType.Document)
            {
                foreach (BsonElement kv in keyVal.AsBsonDocument.Elements)
                    fieldPaths.Add(kv.Name);
            }

            indexes.Add(new CollectionIndexDescriptor(indexName, fieldPaths, unique, sparse));
        }

        return indexes;
    }
}
