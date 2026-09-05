using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Google.Cloud.Firestore;
using Microsoft.Azure.Cosmos;

namespace Fabricate.Infrastructure.Schema;

/// <summary>
/// Aggregate-only profiler for Azure Cosmos DB (#71). Counts come from the server; per-field statistics come
/// from a bounded sample folded into counters as it is read.
/// </summary>
public sealed class CosmosDbDataProfiler : INoSqlDataProfiler
{
    private const int SampleSize = 200;

    public string ProviderName => "cosmosdb";

    public async Task<NoSqlProfileSnapshot> ProfileAsync(
        IReadOnlyList<CollectionMetadata> collections,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collections);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = Environment.GetEnvironmentVariable("COSMOSDB_CONNECTION_STRING")
                ?? throw new InvalidOperationException(
                    "Cosmos DB connection string must be provided or COSMOSDB_CONNECTION_STRING must be set.");
        }

        using var client = new CosmosClient(connectionString);
        var profiles = new List<CollectionProfile>();

        foreach (var metadata in collections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var container = client.GetContainer(metadata.DatabaseName, metadata.CollectionName);
            var accumulator = new NoSqlProfileAccumulator(metadata.QualifiedName);

            // COUNT over the container: an aggregate the server computes, not documents we read.
            long total = 0;
            using (var counter = container.GetItemQueryIterator<long>(new QueryDefinition("SELECT VALUE COUNT(1) FROM c")))
            {
                while (counter.HasMoreResults)
                {
                    foreach (var value in await counter.ReadNextAsync(cancellationToken).ConfigureAwait(false)) total += value;
                }
            }

            var known = metadata.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            using var iterator = container.GetItemQueryIterator<JsonElement>(
                new QueryDefinition($"SELECT TOP {SampleSize} * FROM c"));

            while (iterator.HasMoreResults)
            {
                foreach (var document in await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false))
                {
                    accumulator.BeginDocument();
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    JsonDocumentFolder.Fold(accumulator, document, prefix: null, seen);
                    foreach (var missing in known.Except(seen, StringComparer.Ordinal)) accumulator.MarkAbsent(missing);
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
}

/// <summary>
/// Aggregate-only profiler for Amazon DynamoDB (#71).
/// </summary>
/// <remarks>
/// DynamoDB's item count comes from <c>DescribeTable</c> and is updated roughly every six hours, so it is an
/// estimate by the service's own definition — reported as-is rather than replaced by a full scan, which on a
/// large table would cost more than the profile is worth.
/// </remarks>
public sealed class DynamoDbDataProfiler : INoSqlDataProfiler
{
    private const int SampleSize = 200;

    public string ProviderName => "dynamodb";

    public async Task<NoSqlProfileSnapshot> ProfileAsync(
        IReadOnlyList<CollectionMetadata> collections,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collections);

        // Ambient credentials by default, as the discoverer does — an IAM role rather than a stored key.
        using var client = string.IsNullOrWhiteSpace(connectionString)
            ? new AmazonDynamoDBClient()
            : new AmazonDynamoDBClient(new AmazonDynamoDBConfig { ServiceURL = connectionString });

        var profiles = new List<CollectionProfile>();

        foreach (var metadata in collections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var accumulator = new NoSqlProfileAccumulator(metadata.QualifiedName);
            var described = await client.DescribeTableAsync(metadata.CollectionName, cancellationToken).ConfigureAwait(false);
            var total = described.Table?.ItemCount ?? 0;

            var known = metadata.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            var scan = await client.ScanAsync(
                new ScanRequest { TableName = metadata.CollectionName, Limit = SampleSize },
                cancellationToken).ConfigureAwait(false);

            foreach (var item in scan.Items ?? [])
            {
                accumulator.BeginDocument();
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var (name, value) in item)
                {
                    seen.Add(name);
                    Observe(accumulator, name, value);
                }

                foreach (var missing in known.Except(seen, StringComparer.Ordinal)) accumulator.MarkAbsent(missing);
            }

            var profile = accumulator.Build();
            profiles.Add(profile with { DocumentCount = Math.Max(total, profile.DocumentCount) });
        }

        return new NoSqlProfileSnapshot(
            Guid.NewGuid(), ProviderName,
            collections.FirstOrDefault()?.DatabaseName ?? string.Empty,
            DateTimeOffset.UtcNow, profiles);
    }

    private static void Observe(NoSqlProfileAccumulator accumulator, string path, AttributeValue value)
    {
        if (value.NULL == true)
        {
            accumulator.Observe(path, DocumentFieldType.Null, null);
        }
        else if (value.S is not null)
        {
            accumulator.Observe(path, DocumentFieldType.String, value.S);
        }
        else if (value.N is not null && double.TryParse(value.N, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            accumulator.Observe(path, DocumentFieldType.Number, value.N, NoSqlProfileAccumulator.Sortable(number));
        }
        else if (value.IsBOOLSet)
        {
            accumulator.Observe(path, DocumentFieldType.Boolean, value.BOOL == true ? "true" : "false");
        }
        else if (value.IsMSet)
        {
            accumulator.Observe(path, DocumentFieldType.Object, null);
            foreach (var (name, nested) in value.M) Observe(accumulator, $"{path}.{name}", nested);
        }
        else if (value.IsLSet)
        {
            accumulator.Observe(path, DocumentFieldType.Array, value.L.Count.ToString(CultureInfo.InvariantCulture));
        }
        else if (value.B is not null)
        {
            // The length, never the bytes.
            accumulator.Observe(path, DocumentFieldType.Binary, value.B.Length.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            accumulator.Observe(path, DocumentFieldType.Unknown, null);
        }
    }
}

/// <summary>Aggregate-only profiler for Google Cloud Firestore (#71).</summary>
public sealed class FirestoreDataProfiler : INoSqlDataProfiler
{
    private const int SampleSize = 200;

    public string ProviderName => "firestore";

    public async Task<NoSqlProfileSnapshot> ProfileAsync(
        IReadOnlyList<CollectionMetadata> collections,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collections);

        var projectId = string.IsNullOrWhiteSpace(connectionString)
            ? Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")
                ?? throw new InvalidOperationException(
                    "Firestore project id must be provided or GOOGLE_CLOUD_PROJECT must be set.")
            : connectionString;

        var database = await FirestoreDb.CreateAsync(projectId).ConfigureAwait(false);
        var profiles = new List<CollectionProfile>();

        foreach (var metadata in collections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var accumulator = new NoSqlProfileAccumulator(metadata.QualifiedName);
            var reference = database.Collection(metadata.CollectionName);

            // Firestore's aggregate COUNT is server-side and does not read the documents.
            var counted = await reference.Count().GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var total = counted.Count ?? 0;

            var known = metadata.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            var sample = await reference.Limit(SampleSize).GetSnapshotAsync(cancellationToken).ConfigureAwait(false);

            foreach (var document in sample.Documents)
            {
                accumulator.BeginDocument();
                var seen = new HashSet<string>(StringComparer.Ordinal);

                foreach (var (name, value) in document.ToDictionary())
                {
                    seen.Add(name);
                    Observe(accumulator, name, value);
                }

                foreach (var missing in known.Except(seen, StringComparer.Ordinal)) accumulator.MarkAbsent(missing);
            }

            var profile = accumulator.Build();
            profiles.Add(profile with { DocumentCount = Math.Max(total, profile.DocumentCount) });
        }

        return new NoSqlProfileSnapshot(
            Guid.NewGuid(), ProviderName,
            collections.FirstOrDefault()?.DatabaseName ?? string.Empty,
            DateTimeOffset.UtcNow, profiles);
    }

    private static void Observe(NoSqlProfileAccumulator accumulator, string path, object? value)
    {
        switch (value)
        {
            case null:
                accumulator.Observe(path, DocumentFieldType.Null, null);
                break;
            case string text:
                accumulator.Observe(path, DocumentFieldType.String, text);
                break;
            case bool flag:
                accumulator.Observe(path, DocumentFieldType.Boolean, flag ? "true" : "false");
                break;
            case long or int or double or float:
                var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                accumulator.Observe(path, DocumentFieldType.Number,
                    number.ToString(CultureInfo.InvariantCulture), NoSqlProfileAccumulator.Sortable(number));
                break;
            case Timestamp timestamp:
                accumulator.Observe(path, DocumentFieldType.Date,
                    timestamp.ToDateTimeOffset().ToString("O", CultureInfo.InvariantCulture));
                break;
            case IReadOnlyDictionary<string, object> nested:
                accumulator.Observe(path, DocumentFieldType.Object, null);
                foreach (var (name, child) in nested) Observe(accumulator, $"{path}.{name}", child);
                break;
            case System.Collections.IEnumerable list and not string:
                accumulator.Observe(path, DocumentFieldType.Array, list.Cast<object?>().Count().ToString(CultureInfo.InvariantCulture));
                break;
            default:
                accumulator.Observe(path, DocumentFieldType.Unknown, null);
                break;
        }
    }
}

/// <summary>Folds a JSON document into a profile accumulator, shared by the JSON-shaped providers.</summary>
internal static class JsonDocumentFolder
{
    internal static void Fold(NoSqlProfileAccumulator accumulator, JsonElement element, string? prefix, HashSet<string> seen)
    {
        if (element.ValueKind != JsonValueKind.Object) return;

        foreach (var property in element.EnumerateObject())
        {
            // Cosmos adds its own bookkeeping fields to every document; they say nothing about the customer's data.
            if (property.Name.StartsWith('_')) continue;

            var path = prefix is null ? property.Name : $"{prefix}.{property.Name}";
            seen.Add(path);

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Object:
                    accumulator.Observe(path, DocumentFieldType.Object, null);
                    Fold(accumulator, property.Value, path, seen);
                    break;
                case JsonValueKind.Array:
                    accumulator.Observe(path, DocumentFieldType.Array, property.Value.GetArrayLength().ToString(CultureInfo.InvariantCulture));
                    break;
                case JsonValueKind.String:
                    accumulator.Observe(path, DocumentFieldType.String, property.Value.GetString());
                    break;
                case JsonValueKind.Number:
                    var number = property.Value.GetDouble();
                    accumulator.Observe(path, DocumentFieldType.Number,
                        number.ToString(CultureInfo.InvariantCulture), NoSqlProfileAccumulator.Sortable(number));
                    break;
                case JsonValueKind.True or JsonValueKind.False:
                    accumulator.Observe(path, DocumentFieldType.Boolean, property.Value.GetBoolean() ? "true" : "false");
                    break;
                case JsonValueKind.Null or JsonValueKind.Undefined:
                    accumulator.Observe(path, DocumentFieldType.Null, null);
                    break;
                default:
                    accumulator.Observe(path, DocumentFieldType.Unknown, null);
                    break;
            }
        }
    }
}

/// <summary>Resolves the profiler for a provider name, mirroring <see cref="NoSqlSchemaDiscovererFactory"/>.</summary>
public sealed class NoSqlDataProfilerFactory(IEnumerable<INoSqlDataProfiler> profilers) : INoSqlDataProfilerFactory
{
    public INoSqlDataProfiler GetProfiler(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        return profilers.FirstOrDefault(p => string.Equals(p.ProviderName, providerName, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotSupportedException(
                $"No NoSQL profiler for provider '{providerName}'. Supported: {string.Join(", ", profilers.Select(p => p.ProviderName))}.");
    }
}
