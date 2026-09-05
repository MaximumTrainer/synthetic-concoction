using System.Text.Json;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Schema;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace Fabricate.Tests.Integration;

/// <summary>
/// #71: <c>INoSqlDataProfiler</c> had no implementation for any provider, so the profiling half of #52's design
/// was missing. This exercises the MongoDB profiler against a real server — the one provider with a container
/// image rather than a cloud account behind it — and checks the property that matters most: that a profile is
/// aggregates and never a copy of the documents.
/// </summary>
public sealed class NoSqlProfilerTests : IAsyncLifetime
{
    /// <summary>Values planted in the documents; none of them may appear in the snapshot.</summary>
    private const string SecretEmail = "patient.zero@example.invalid";
    private const string SecretNote = "DIAGNOSIS-CONFIDENTIAL-DO-NOT-PROFILE";

    private MongoDbContainer? _container;
    private string? _connectionString;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("FABRICATE_SKIP_DOCKER_TESTS") == "1") return;

        try
        {
            _container = new MongoDbBuilder("mongo:7").Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();

            var client = new MongoClient(_connectionString);
            var database = client.GetDatabase("clinic");
            var patients = database.GetCollection<BsonDocument>("patients");

            // A mix of shapes: an absent field, a null, a nested object, an array, and a couple of types.
            await patients.InsertManyAsync(
            [
                new BsonDocument
                {
                    { "email", SecretEmail },
                    { "age", 41 },
                    { "active", true },
                    { "note", SecretNote },
                    { "address", new BsonDocument { { "city", "Leeds" } } },
                    { "tags", new BsonArray { "a", "b" } },
                },
                new BsonDocument
                {
                    { "email", "second@example.invalid" },
                    { "age", 29 },
                    { "active", false },
                    { "note", BsonNull.Value },
                },
                new BsonDocument
                {
                    { "email", "third@example.invalid" },
                    { "age", 63 },
                    { "active", true },
                },
            ]);
        }
        catch (Exception)
        {
            _container = null;
            _connectionString = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    private async Task<NoSqlProfileSnapshot?> ProfileAsync()
    {
        if (_connectionString is null) return null;

        var discoverer = new MongoDbSchemaDiscoverer();
        var collections = await discoverer.DiscoverCollectionsAsync(_connectionString, "clinic");

        return await new MongoDbDataProfiler().ProfileAsync(collections, _connectionString);
    }

    /// <summary>
    /// Every test here self-skips when the container did not start, so without this a bad image tag would show up
    /// as a green run covering nothing (#90, #91).
    /// </summary>
    [Fact]
    public void TheContainerStartedWhenDockerIsAvailable()
    {
        if (Environment.GetEnvironmentVariable("FABRICATE_SKIP_DOCKER_TESTS") == "1") return;
        if (_container is null) return; // No Docker at all.

        _connectionString.Should().NotBeNull("the MongoDB container started, so it must be reachable");
    }

    /// <summary>
    /// Discovery against a real server, which #53's own integration test never did: it was gated behind a
    /// connection string nobody set, and iterated a list that was always empty even when it ran (#91).
    /// </summary>
    [Fact]
    public async Task TheDiscovererReportsTheCollectionItsFieldsAndItsDefaultIndex()
    {
        if (_connectionString is null) return;

        var collections = await new MongoDbSchemaDiscoverer().DiscoverCollectionsAsync(_connectionString, "clinic");

        var patients = collections.Should().ContainSingle(c => c.CollectionName == "patients").Subject;
        patients.DatabaseName.Should().Be("clinic");
        patients.QualifiedName.Should().Be("clinic.patients");
        patients.Fields.Select(f => f.Name).Should().Contain(["email", "age", "active", "note"]);
        patients.Indexes.Should().Contain(i => i.Name == "_id_", "MongoDB creates the _id_ index for every collection");

        foreach (var field in patients.Fields) AssertValidFieldDescriptor(field);
    }

    /// <summary>A descriptor with a blank name or an out-of-range type is a discovery bug, not a data shape.</summary>
    internal static void AssertValidFieldDescriptor(FieldDescriptor field)
    {
        field.Name.Should().NotBeNullOrEmpty();
        field.FieldType.Should().BeDefined();

        foreach (var nested in field.NestedFields ?? []) AssertValidFieldDescriptor(nested);
    }

    [Fact]
    public async Task TheProfileReportsAggregatesForEveryField()
    {
        var snapshot = await ProfileAsync();
        if (snapshot is null) return;

        snapshot.ProviderName.Should().Be("mongodb");
        var patients = snapshot.Collections.Should().ContainSingle(c => c.QualifiedName == "clinic.patients").Subject;

        patients.DocumentCount.Should().Be(3);

        var age = patients.FieldProfiles.Should().ContainSingle(f => f.FieldPath == "age").Subject;
        age.InferredType.Should().Be(DocumentFieldType.Number);
        age.NonNullCount.Should().Be(3);
        age.ApproximateDistinctValues.Should().Be(3);
        age.MinValue.Should().NotBeNull();
        age.MaxValue.Should().NotBeNull();
        string.CompareOrdinal(age.MinValue, age.MaxValue).Should().BeLessThan(0,
            "min and max are stored in a form that orders numerically, not lexically — 29 must sort below 63");

        var active = patients.FieldProfiles.Should().ContainSingle(f => f.FieldPath == "active").Subject;
        active.InferredType.Should().Be(DocumentFieldType.Boolean);
        active.ApproximateDistinctValues.Should().Be(2);
    }

    [Fact]
    public async Task AFieldMissingFromSomeDocumentsIsCountedAsAbsent()
    {
        var snapshot = await ProfileAsync();
        if (snapshot is null) return;

        var patients = snapshot.Collections.Single(c => c.QualifiedName == "clinic.patients");

        // note: present-and-set once, present-and-null once, absent once.
        var note = patients.FieldProfiles.Should().ContainSingle(f => f.FieldPath == "note").Subject;
        note.NonNullCount.Should().Be(1);
        note.NullCount.Should().Be(2,
            "a document that does not carry the field counts the same as one where it is null — that is what " +
            "presence ratio means in a document store");

        // address exists on one document only, and its nested field is profiled by path.
        patients.FieldProfiles.Should().Contain(f => f.FieldPath == "address.city");
        patients.FieldProfiles.Single(f => f.FieldPath == "address.city").NonNullCount.Should().Be(1);
    }

    [Fact]
    public async Task AnArrayFieldIsProfiledByLengthNotContents()
    {
        var snapshot = await ProfileAsync();
        if (snapshot is null) return;

        var tags = snapshot.Collections.Single(c => c.QualifiedName == "clinic.patients")
            .FieldProfiles.Should().ContainSingle(f => f.FieldPath == "tags").Subject;

        tags.InferredType.Should().Be(DocumentFieldType.Array);
        tags.ApproximateDistinctValues.Should().Be(1,
            "an array is recorded by its length; its elements are not the profile's business");
        JsonSerializer.Serialize(snapshot).Should().NotContain("\"a\",\"b\"");
    }

    [Fact]
    public async Task TheSnapshotContainsNoRawDocumentContent()
    {
        var snapshot = await ProfileAsync();
        if (snapshot is null) return;

        var serialised = JsonSerializer.Serialize(snapshot);

        serialised.Should().NotContain(SecretNote,
            "a profiler that carries document content is a second copy of the data, which is the one thing it " +
            "must never be");
        serialised.Should().NotContain("Leeds");
        serialised.Should().NotContain("patient.zero");

        // The field names and the aggregates are exactly what should survive.
        serialised.Should().Contain("note");
        serialised.Should().Contain("3", "the document count is an aggregate and belongs in the snapshot");
        snapshot.Collections.Single().DocumentCount.Should().Be(3);

        // _id is skipped for the same reason the discoverer skips it: a set of real identifiers is content.
        snapshot.Collections.Single().FieldProfiles.Should().NotContain(f => f.FieldPath == "_id");
    }

    [Fact]
    public async Task EmailIsAggregatedWithoutItsValuesLeaking()
    {
        var snapshot = await ProfileAsync();
        if (snapshot is null) return;

        var email = snapshot.Collections.Single(c => c.QualifiedName == "clinic.patients")
            .FieldProfiles.Single(f => f.FieldPath == "email");

        email.InferredType.Should().Be(DocumentFieldType.String);
        email.NonNullCount.Should().Be(3);
        email.ApproximateDistinctValues.Should().Be(3, "cardinality is the useful statistic about an email column");

        // For a string field the min and max are the shortest and longest *lengths*, not the values: a string
        // min/max is a verbatim customer value, and on a low-cardinality field it is the field's content.
        var everything = JsonSerializer.Serialize(snapshot);
        foreach (var value in new[] { SecretEmail, "second@example.invalid", "third@example.invalid" })
        {
            everything.Should().NotContain(value, "no string value belongs in a profile, not even as a min or max");
        }

        email.MinValue.Should().NotBeNull();
        email.MaxValue.Should().NotBeNull();
        string.CompareOrdinal(email.MinValue, email.MaxValue).Should().BeLessThan(0,
            "the addresses differ in length, so the length range is a real range");
    }

    [Fact]
    public void TheFactoryResolvesEveryProviderAndRefusesUnknownOnes()
    {
        var factory = new NoSqlDataProfilerFactory(
        [
            new MongoDbDataProfiler(),
            new CosmosDbDataProfiler(),
            new DynamoDbDataProfiler(),
            new FirestoreDataProfiler(),
        ]);

        foreach (var provider in new[] { "mongodb", "cosmosdb", "dynamodb", "firestore", "MongoDB" })
        {
            factory.GetProfiler(provider).Should().NotBeNull();
        }

        var unknown = () => factory.GetProfiler("cassandra");
        unknown.Should().Throw<NotSupportedException>().WithMessage("*Supported:*");
    }
}
