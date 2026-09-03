using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Schema;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fabricate.Tests.Application;

public sealed class NoSqlProviderTests
{
    // ── Domain model tests ─────────────────────────────────────────────────────

    [Fact]
    public void CollectionMetadata_QualifiedName_CombinesDatabaseAndCollectionName()
    {
        var metadata = new CollectionMetadata(
            DatabaseName: "mydb",
            CollectionName: "users",
            Fields: [],
            PartitionKey: null,
            Indexes: []);

        metadata.QualifiedName.Should().Be("mydb.users");
    }

    [Fact]
    public void FieldDescriptor_WithNestedFields_ReturnsCorrectHierarchy()
    {
        var nested = new FieldDescriptor("city", DocumentFieldType.String, IsNullable: false);
        var address = new FieldDescriptor("address", DocumentFieldType.Object, IsNullable: true, NestedFields: [nested]);

        address.NestedFields.Should().HaveCount(1);
        address.NestedFields![0].Name.Should().Be("city");
        address.NestedFields![0].FieldType.Should().Be(DocumentFieldType.String);
    }

    [Fact]
    public void PartitionKeyDescriptor_StoresFieldPathAndKeyType()
    {
        var pk = new PartitionKeyDescriptor("/userId", "Hash");

        pk.FieldPath.Should().Be("/userId");
        pk.KeyType.Should().Be("Hash");
    }

    [Fact]
    public void CollectionIndexDescriptor_StoresIndexDetails()
    {
        var index = new CollectionIndexDescriptor("idx_email", ["email"], IsUnique: true, IsSparse: false);

        index.Name.Should().Be("idx_email");
        index.FieldPaths.Should().ContainSingle("email");
        index.IsUnique.Should().BeTrue();
    }

    // ── Adapter tests ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("cosmosdb")]
    [InlineData("mongodb")]
    [InlineData("dynamodb")]
    [InlineData("firestore")]
    public void Adapter_ProviderName_MatchesExpected(string providerName)
    {
        INoSqlSchemaDiscoverer adapter = providerName switch
        {
            "cosmosdb" => new CosmosDbSchemaDiscoverer(),
            "mongodb" => new MongoDbSchemaDiscoverer(),
            "dynamodb" => new DynamoDbSchemaDiscoverer(),
            "firestore" => new FirestoreSchemaDiscoverer(),
            _ => throw new ArgumentException($"Unknown provider: {providerName}")
        };

        adapter.ProviderName.Should().Be(providerName);
    }

    [Fact]
    public async Task CosmosDbAdapter_ThrowsInvalidOperationException_WhenNoConnectionString()
    {
        var adapter = new CosmosDbSchemaDiscoverer();
        var savedEnv = Environment.GetEnvironmentVariable("COSMOSDB_CONNECTION_STRING");
        Environment.SetEnvironmentVariable("COSMOSDB_CONNECTION_STRING", null);
        try
        {
            var act = async () => await adapter.DiscoverCollectionsAsync(string.Empty, "mydb");
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*COSMOSDB_CONNECTION_STRING*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("COSMOSDB_CONNECTION_STRING", savedEnv);
        }
    }

    [Fact]
    public async Task MongoDbAdapter_ThrowsInvalidOperationException_WhenNoConnectionString()
    {
        var adapter = new MongoDbSchemaDiscoverer();
        var savedEnv = Environment.GetEnvironmentVariable("MONGODB_CONNECTION_STRING");
        Environment.SetEnvironmentVariable("MONGODB_CONNECTION_STRING", null);
        try
        {
            var act = async () => await adapter.DiscoverCollectionsAsync(string.Empty, "mydb");
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*MONGODB_CONNECTION_STRING*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("MONGODB_CONNECTION_STRING", savedEnv);
        }
    }

    [Fact]
    public async Task FirestoreAdapter_ThrowsInvalidOperationException_WhenNoConnectionString()
    {
        var adapter = new FirestoreSchemaDiscoverer();
        var savedEnv = Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT");
        Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT", null);
        try
        {
            var act = async () => await adapter.DiscoverCollectionsAsync(string.Empty, string.Empty);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*GOOGLE_CLOUD_PROJECT*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GOOGLE_CLOUD_PROJECT", savedEnv);
        }
    }

    // ── Factory tests ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("cosmosdb")]
    [InlineData("CosmosDB")]   // case-insensitive
    [InlineData("mongodb")]
    [InlineData("MongoDB")]
    [InlineData("dynamodb")]
    [InlineData("firestore")]
    public void Factory_GetDiscoverer_ResolvesRegisteredProviders(string providerName)
    {
        var factory = BuildFactory();

        var discoverer = factory.GetDiscoverer(providerName);

        discoverer.Should().NotBeNull();
    }

    [Fact]
    public void Factory_GetDiscoverer_ThrowsForUnknownProvider()
    {
        var factory = BuildFactory();

        var act = () => factory.GetDiscoverer("oracle");

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*oracle*");
    }

    [Fact]
    public void Factory_GetDiscoverer_MessageIncludesKnownProviders()
    {
        var factory = BuildFactory();

        var act = () => factory.GetDiscoverer("cassandra");

        act.Should().Throw<NotSupportedException>()
            .WithMessage("*cosmosdb*");
    }

    private static INoSqlSchemaDiscovererFactory BuildFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<INoSqlSchemaDiscoverer, CosmosDbSchemaDiscoverer>();
        services.AddSingleton<INoSqlSchemaDiscoverer, MongoDbSchemaDiscoverer>();
        services.AddSingleton<INoSqlSchemaDiscoverer, DynamoDbSchemaDiscoverer>();
        services.AddSingleton<INoSqlSchemaDiscoverer, FirestoreSchemaDiscoverer>();
        services.AddSingleton<INoSqlSchemaDiscovererFactory, NoSqlSchemaDiscovererFactory>();
        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<INoSqlSchemaDiscovererFactory>();
    }
}
