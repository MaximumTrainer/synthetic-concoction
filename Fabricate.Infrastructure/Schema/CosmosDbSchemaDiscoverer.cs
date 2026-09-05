using System.Text.Json;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Microsoft.Azure.Cosmos;

namespace Fabricate.Infrastructure.Schema;

/// <summary>
/// Azure Cosmos DB schema discovery adapter.
/// Samples up to 200 documents per container to infer field types.
/// Reads the partition key and composite index definitions from container metadata.
/// </summary>
/// <remarks>
/// Authentication: provide a full Cosmos DB connection string in <c>connectionString</c>,
/// or set the <c>COSMOSDB_CONNECTION_STRING</c> environment variable.
/// Managed Identity is supported automatically when the Azure SDK default credential chain
/// can resolve credentials.
/// </remarks>
public sealed class CosmosDbSchemaDiscoverer : INoSqlSchemaDiscoverer
{
    public string ProviderName => "cosmosdb";

    public async Task<IReadOnlyList<CollectionMetadata>> DiscoverCollectionsAsync(
        string connectionString,
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            connectionString = Environment.GetEnvironmentVariable("COSMOSDB_CONNECTION_STRING")
                ?? throw new InvalidOperationException(
                    "CosmosDB connection string must be provided or COSMOSDB_CONNECTION_STRING environment variable must be set.");

        using var client = CosmosConnectionString.CreateClient(connectionString);

        var database = client.GetDatabase(databaseName);
        var results = new List<CollectionMetadata>();

        FeedIterator<ContainerProperties> containerIterator = database.GetContainerQueryIterator<ContainerProperties>();
        while (containerIterator.HasMoreResults)
        {
            FeedResponse<ContainerProperties> page = await containerIterator.ReadNextAsync(cancellationToken);
            foreach (ContainerProperties props in page)
            {
                var acc = new FieldInferenceHelper.FieldAccumulator();
                await SampleDocumentsAsync(database.GetContainer(props.Id), acc, cancellationToken);

                results.Add(new CollectionMetadata(
                    databaseName,
                    props.Id,
                    acc.Build(),
                    BuildPartitionKey(props),
                    BuildIndexes(props)));
            }
        }

        return results;
    }

    private static async Task SampleDocumentsAsync(
        Container container,
        FieldInferenceHelper.FieldAccumulator acc,
        CancellationToken cancellationToken)
    {
        FeedIterator<JsonElement> docIterator = container.GetItemQueryIterator<JsonElement>(
            "SELECT TOP 200 * FROM c");
        while (docIterator.HasMoreResults)
        {
            FeedResponse<JsonElement> page = await docIterator.ReadNextAsync(cancellationToken);
            foreach (JsonElement doc in page)
                InferFieldsFromJson(acc, doc);
        }
    }

    private static void InferFieldsFromJson(FieldInferenceHelper.FieldAccumulator acc, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (JsonProperty prop in element.EnumerateObject())
        {
            // Skip internal Cosmos system properties
            if (prop.Name.StartsWith('_')) continue;

            bool isNull = prop.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
            DocumentFieldType type = prop.Value.ValueKind switch
            {
                JsonValueKind.String => DocumentFieldType.String,
                JsonValueKind.Number => DocumentFieldType.Number,
                JsonValueKind.True => DocumentFieldType.Boolean,
                JsonValueKind.False => DocumentFieldType.Boolean,
                JsonValueKind.Object => DocumentFieldType.Object,
                JsonValueKind.Array => DocumentFieldType.Array,
                _ => DocumentFieldType.Null
            };

            if (type == DocumentFieldType.Object)
                acc.Observe(prop.Name, type, isNull, nested => InferFieldsFromJson(nested, prop.Value));
            else
                acc.Observe(prop.Name, type, isNull);
        }
    }

    private static PartitionKeyDescriptor? BuildPartitionKey(ContainerProperties props)
    {
        var paths = props.PartitionKeyPaths;
        if (paths is { Count: > 0 })
            return new PartitionKeyDescriptor(
                string.Join(",", paths.Select(p => p.TrimStart('/'))),
                "hash");

        if (!string.IsNullOrEmpty(props.PartitionKeyPath))
            return new PartitionKeyDescriptor(props.PartitionKeyPath.TrimStart('/'), "hash");

        return null;
    }

    private static IReadOnlyList<CollectionIndexDescriptor> BuildIndexes(ContainerProperties props)
    {
        var indexes = new List<CollectionIndexDescriptor>();
        if (props.IndexingPolicy?.CompositeIndexes is null) return indexes;

        int i = 0;
        foreach (var composite in props.IndexingPolicy.CompositeIndexes)
        {
            var fieldPaths = composite.Select(p => p.Path.TrimStart('/')).ToList();
            indexes.Add(new CollectionIndexDescriptor($"composite_{i++}", fieldPaths, false));
        }

        return indexes;
    }
}
