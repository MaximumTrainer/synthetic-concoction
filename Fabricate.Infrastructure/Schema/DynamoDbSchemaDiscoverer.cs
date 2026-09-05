using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Schema;

/// <summary>
/// AWS DynamoDB schema discovery adapter.
/// Lists all tables (optionally filtered by a name prefix via <c>databaseName</c>),
/// retrieves table metadata (key schema, GSIs, LSIs), and samples up to 200 items per
/// table to infer attribute names and types.
/// </summary>
/// <remarks>
/// Authentication: standard AWS credential resolution chain (IAM role, environment variables,
/// shared credentials file). For local DynamoDB use <c>serviceUrl=http://localhost:8000</c>
/// in the connection string.
///
/// Connection string format: semicolon-separated key=value pairs, e.g.
/// <c>region=us-east-1</c> or <c>region=us-east-1;serviceUrl=http://localhost:8000</c>.
/// Falls back to the <c>AWS_DEFAULT_REGION</c> environment variable, then <c>us-east-1</c>.
/// </remarks>
public sealed class DynamoDbSchemaDiscoverer : INoSqlSchemaDiscoverer
{
    public string ProviderName => "dynamodb";

    public async Task<IReadOnlyList<CollectionMetadata>> DiscoverCollectionsAsync(
        string connectionString,
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(connectionString);

        var tableNames = await ListAllTablesAsync(client, cancellationToken);

        // Optionally filter tables by name prefix when databaseName is provided
        if (!string.IsNullOrWhiteSpace(databaseName))
            tableNames = tableNames.Where(n => n.StartsWith(databaseName, StringComparison.OrdinalIgnoreCase)).ToList();

        var results = new List<CollectionMetadata>();
        foreach (var tableName in tableNames)
        {
            var describeResponse = await client.DescribeTableAsync(
                new DescribeTableRequest { TableName = tableName },
                cancellationToken);

            TableDescription table = describeResponse.Table;

            var acc = new FieldInferenceHelper.FieldAccumulator();

            // Seed field types from the attribute definitions (key and index attributes)
            foreach (var attr in table.AttributeDefinitions)
            {
                DocumentFieldType type = attr.AttributeType.Value switch
                {
                    "S" => DocumentFieldType.String,
                    "N" => DocumentFieldType.Number,
                    "B" => DocumentFieldType.Binary,
                    _ => DocumentFieldType.Unknown
                };
                acc.Observe(attr.AttributeName, type, false);
            }

            // Sample items to discover non-key attributes
            var scanResponse = await client.ScanAsync(
                new ScanRequest { TableName = tableName, Limit = 200 },
                cancellationToken);

            foreach (var item in scanResponse.Items)
                InferFieldsFromAttributes(acc, item);

            PartitionKeyDescriptor? partitionKey = BuildPartitionKey(table);
            IReadOnlyList<CollectionIndexDescriptor> indexes = BuildIndexes(table);

            string ns = string.IsNullOrWhiteSpace(databaseName) ? "dynamodb" : databaseName;
            results.Add(new CollectionMetadata(ns, tableName, acc.Build(), partitionKey, indexes));
        }

        return results;
    }

    private static AmazonDynamoDBClient CreateClient(string connectionString)
        => DynamoDbConnectionString.CreateClient(connectionString);

    private static async Task<List<string>> ListAllTablesAsync(
        AmazonDynamoDBClient client,
        CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        string? lastTableName = null;

        do
        {
            var request = new ListTablesRequest { ExclusiveStartTableName = lastTableName };
            var response = await client.ListTablesAsync(request, cancellationToken);
            tables.AddRange(response.TableNames);
            lastTableName = response.LastEvaluatedTableName;
        } while (lastTableName is not null);

        return tables;
    }

    private static void InferFieldsFromAttributes(
        FieldInferenceHelper.FieldAccumulator acc,
        Dictionary<string, AttributeValue> attributes)
    {
        foreach (var (name, attr) in attributes)
        {
            var (type, isNull) = GetAttributeType(attr);
            if (type == DocumentFieldType.Object && attr.IsMSet && attr.M is not null)
                acc.Observe(name, type, isNull, nested => InferFieldsFromAttributes(nested, attr.M));
            else
                acc.Observe(name, type, isNull);
        }
    }

    private static (DocumentFieldType Type, bool IsNull) GetAttributeType(AttributeValue attr)
    {
        if (attr.NULL == true) return (DocumentFieldType.Null, true);
        if (attr.S is not null) return (DocumentFieldType.String, false);
        if (attr.N is not null) return (DocumentFieldType.Number, false);
        if (attr.B is not null) return (DocumentFieldType.Binary, false);
        if (attr.IsBOOLSet) return (DocumentFieldType.Boolean, false);
        if (attr.IsLSet) return (DocumentFieldType.Array, false);
        if (attr.IsMSet) return (DocumentFieldType.Object, false);
        if (attr.SS?.Count > 0) return (DocumentFieldType.Array, false);
        if (attr.NS?.Count > 0) return (DocumentFieldType.Array, false);
        if (attr.BS?.Count > 0) return (DocumentFieldType.Array, false);
        return (DocumentFieldType.Unknown, false);
    }

    private static PartitionKeyDescriptor? BuildPartitionKey(TableDescription table)
    {
        var hashKey = table.KeySchema.FirstOrDefault(k => k.KeyType.Value == "HASH");
        if (hashKey is null) return null;

        var rangeKey = table.KeySchema.FirstOrDefault(k => k.KeyType.Value == "RANGE");
        string fieldPath = rangeKey is null
            ? hashKey.AttributeName
            : $"{hashKey.AttributeName},{rangeKey.AttributeName}";

        return new PartitionKeyDescriptor(fieldPath, "hash");
    }

    private static IReadOnlyList<CollectionIndexDescriptor> BuildIndexes(TableDescription table)
    {
        var indexes = new List<CollectionIndexDescriptor>();

        if (table.GlobalSecondaryIndexes is not null)
        {
            foreach (var gsi in table.GlobalSecondaryIndexes)
            {
                var paths = gsi.KeySchema.Select(k => k.AttributeName).ToList();
                indexes.Add(new CollectionIndexDescriptor(gsi.IndexName, paths, false));
            }
        }

        if (table.LocalSecondaryIndexes is not null)
        {
            foreach (var lsi in table.LocalSecondaryIndexes)
            {
                var paths = lsi.KeySchema.Select(k => k.AttributeName).ToList();
                indexes.Add(new CollectionIndexDescriptor(lsi.IndexName, paths, false));
            }
        }

        return indexes;
    }
}
