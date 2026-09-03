using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Schema;
using FluentAssertions;
using Xunit;

namespace Fabricate.Tests.Integration;

/// <summary>
/// Integration tests for the NoSQL schema discovery adapters.
/// Each test requires a specific environment variable to be set — without it, the test
/// returns early (same pattern as other integration tests in this project).
///
/// Required environment variables:
///   COSMOSDB_CONNECTION_STRING  — Azure Cosmos DB (SQL API) connection string
///   MONGODB_CONNECTION_STRING   — MongoDB connection string (e.g. mongodb://localhost:27017)
///   AWS_DEFAULT_REGION          — AWS region (e.g. us-east-1); credentials via IAM chain
///   GOOGLE_CLOUD_PROJECT        — GCP project ID; credentials via GOOGLE_APPLICATION_CREDENTIALS or ADC
/// </summary>
public sealed class NoSqlAdapterIntegrationTests
{
    // ── Cosmos DB ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CosmosDb_DiscoverCollectionsAsync_ReturnsCollections()
    {
        var connectionString = Environment.GetEnvironmentVariable("COSMOSDB_CONNECTION_STRING");
        var databaseName = Environment.GetEnvironmentVariable("COSMOSDB_TEST_DATABASE") ?? "fabricate_test";
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var adapter = new CosmosDbSchemaDiscoverer();
        var collections = await adapter.DiscoverCollectionsAsync(connectionString, databaseName);

        collections.Should().NotBeNull();
        foreach (var collection in collections)
        {
            collection.DatabaseName.Should().Be(databaseName);
            collection.CollectionName.Should().NotBeNullOrEmpty();
            collection.Fields.Should().NotBeNull();
            collection.Indexes.Should().NotBeNull();
            collection.QualifiedName.Should().Be($"{databaseName}.{collection.CollectionName}");

            foreach (var field in collection.Fields)
                AssertValidFieldDescriptor(field);
        }
    }

    [Fact]
    public async Task CosmosDb_DiscoverCollectionsAsync_IncludesPartitionKeyWhenPresent()
    {
        var connectionString = Environment.GetEnvironmentVariable("COSMOSDB_CONNECTION_STRING");
        var databaseName = Environment.GetEnvironmentVariable("COSMOSDB_TEST_DATABASE") ?? "fabricate_test";
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var adapter = new CosmosDbSchemaDiscoverer();
        var collections = await adapter.DiscoverCollectionsAsync(connectionString, databaseName);

        // All Cosmos DB containers must have a partition key
        foreach (var collection in collections)
            collection.PartitionKey.Should().NotBeNull(
                $"Cosmos DB container '{collection.CollectionName}' should always have a partition key");
    }

    // ── MongoDB ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task MongoDB_DiscoverCollectionsAsync_ReturnsCollections()
    {
        var connectionString = Environment.GetEnvironmentVariable("MONGODB_CONNECTION_STRING");
        var databaseName = Environment.GetEnvironmentVariable("MONGODB_TEST_DATABASE") ?? "fabricate_test";
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var adapter = new MongoDbSchemaDiscoverer();
        var collections = await adapter.DiscoverCollectionsAsync(connectionString, databaseName);

        collections.Should().NotBeNull();
        foreach (var collection in collections)
        {
            collection.DatabaseName.Should().Be(databaseName);
            collection.CollectionName.Should().NotBeNullOrEmpty();
            collection.Fields.Should().NotBeNull();
            collection.Indexes.Should().NotBeNull();

            foreach (var field in collection.Fields)
                AssertValidFieldDescriptor(field);
        }
    }

    [Fact]
    public async Task MongoDB_DiscoverCollectionsAsync_IndexesIncludeDefaultIdIndex()
    {
        var connectionString = Environment.GetEnvironmentVariable("MONGODB_CONNECTION_STRING");
        var databaseName = Environment.GetEnvironmentVariable("MONGODB_TEST_DATABASE") ?? "fabricate_test";
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var adapter = new MongoDbSchemaDiscoverer();
        var collections = await adapter.DiscoverCollectionsAsync(connectionString, databaseName);

        foreach (var collection in collections)
        {
            collection.Indexes.Should().Contain(
                idx => idx.Name == "_id_",
                $"Collection '{collection.CollectionName}' should always have the default _id_ index");
        }
    }

    // ── DynamoDB ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DynamoDB_DiscoverCollectionsAsync_ReturnsTables()
    {
        var region = Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");
        if (string.IsNullOrWhiteSpace(region)) return;

        var tablePrefix = Environment.GetEnvironmentVariable("DYNAMODB_TABLE_PREFIX") ?? string.Empty;
        var adapter = new DynamoDbSchemaDiscoverer();

        var collections = await adapter.DiscoverCollectionsAsync(
            $"region={region}",
            tablePrefix);

        collections.Should().NotBeNull();
        foreach (var collection in collections)
        {
            collection.CollectionName.Should().NotBeNullOrEmpty();
            collection.Fields.Should().NotBeNull();
            collection.Indexes.Should().NotBeNull();

            // DynamoDB tables always have a hash key partition key
            collection.PartitionKey.Should().NotBeNull(
                $"DynamoDB table '{collection.CollectionName}' should always have a partition key");

            foreach (var field in collection.Fields)
                AssertValidFieldDescriptor(field);
        }
    }

    [Fact]
    public async Task DynamoDB_DiscoverCollectionsAsync_SupportsLocalDynamoDb()
    {
        var serviceUrl = Environment.GetEnvironmentVariable("DYNAMODB_LOCAL_URL");
        if (string.IsNullOrWhiteSpace(serviceUrl)) return;

        var adapter = new DynamoDbSchemaDiscoverer();
        var collections = await adapter.DiscoverCollectionsAsync(
            $"region=us-east-1;serviceUrl={serviceUrl}",
            string.Empty);

        collections.Should().NotBeNull();
    }

    // ── Firestore ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Firestore_DiscoverCollectionsAsync_ReturnsCollections()
    {
        var projectId = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");
        if (string.IsNullOrWhiteSpace(projectId)) return;

        var adapter = new FirestoreSchemaDiscoverer();
        var collections = await adapter.DiscoverCollectionsAsync(projectId, string.Empty);

        collections.Should().NotBeNull();
        foreach (var collection in collections)
        {
            collection.CollectionName.Should().NotBeNullOrEmpty();
            collection.Fields.Should().NotBeNull();
            collection.Indexes.Should().NotBeNull();
            collection.PartitionKey.Should().BeNull("Firestore manages partitioning internally");

            foreach (var field in collection.Fields)
                AssertValidFieldDescriptor(field);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AssertValidFieldDescriptor(FieldDescriptor field)
    {
        field.Name.Should().NotBeNullOrEmpty();
        field.FieldType.Should().BeDefined();

        if (field.NestedFields is not null)
            foreach (var nested in field.NestedFields)
                AssertValidFieldDescriptor(nested);
    }
}
