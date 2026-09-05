using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Schema;
using FluentAssertions;
using Testcontainers.DynamoDb;
using Xunit.Abstractions;

namespace Fabricate.Tests.Integration;

/// <summary>
/// #91: the DynamoDB and Firestore discoverers (#55, #56) and profilers (#71) had never run against a database.
/// Their only integration tests were gated behind cloud credentials nobody had configured, so a green suite said
/// nothing at all about them.
///
/// <para>
/// Both providers have emulators, so neither needs an account. Each is seeded with the same shaped documents the
/// MongoDB suite uses — a field present on one document, explicitly null on another and absent from a third; a
/// nested object; an array — so a difference between providers is a real difference and not a difference of
/// fixture. The assertions are the MongoDB suite's assertions, in <see cref="AssertSharedProfileShape"/>.
/// </para>
/// </summary>
[Collection("NoSqlEmulators")]
public sealed class NoSqlEmulatorTests(NoSqlEmulatorFixture fixture, ITestOutputHelper output)
    : IClassFixture<NoSqlEmulatorFixture>
{
    private const string Collection = NoSqlEmulatorFixture.Collection;

    // ── the guard ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every test below self-skips when its emulator did not start, so without this a bad image reference or a
    /// changed emulator log line would show up as a green run covering nothing — which is the failure this issue
    /// exists to correct, and which #90 hit for real.
    /// </summary>
    [Fact]
    public void BothEmulatorsStartedWhenDockerIsAvailable()
    {
        output.WriteLine(fixture.Report());

        if (!fixture.DockerAvailable) return;

        fixture.DynamoConnectionString.Should().NotBeNull(
            "DynamoDB Local must start when Docker is available; it failed with: {0}", fixture.DynamoFailure);
        fixture.FirestoreProjectId.Should().NotBeNull(
            "the Firestore emulator must start when Docker is available; it failed with: {0}", fixture.FirestoreFailure);

        // Opting in is a statement that Cosmos should be covered on this run, so a Cosmos that failed to start is
        // a failure rather than a skip — otherwise the weekly job goes green having verified nothing.
        if (Environment.GetEnvironmentVariable("FABRICATE_COSMOS_EMULATOR") == "1")
        {
            fixture.CosmosConnectionString.Should().NotBeNull(
                "FABRICATE_COSMOS_EMULATOR=1 asked for the Cosmos emulator; it failed with: {0}", fixture.CosmosFailure);
        }
    }

    // ── DynamoDB ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DynamoDb_DiscoversTheTableItsKeyAndItsFields()
    {
        if (fixture.DynamoConnectionString is null) return;

        var collections = await new DynamoDbSchemaDiscoverer()
            .DiscoverCollectionsAsync(fixture.DynamoConnectionString, Collection);

        var table = collections.Should().ContainSingle().Subject;
        table.CollectionName.Should().Be(Collection);
        table.Fields.Select(f => f.Name).Should().Contain(["id", "email", "age", "active", "note"]);
        table.PartitionKey.Should().NotBeNull("a DynamoDB table always has a hash key");
        table.PartitionKey!.FieldPath.Should().Be("id");

        foreach (var field in table.Fields) NoSqlProfilerTests.AssertValidFieldDescriptor(field);
    }

    [Fact]
    public async Task DynamoDb_ProfilesAggregatesWithoutDocumentContent()
    {
        if (fixture.DynamoConnectionString is null) return;

        var snapshot = await ProfileAsync(
            new DynamoDbSchemaDiscoverer(), new DynamoDbDataProfiler(), fixture.DynamoConnectionString, Collection);

        AssertSharedProfileShape(snapshot, "dynamodb");
    }

    /// <summary>
    /// The inconsistency #91 turned up first: the discoverer parsed <c>region=...;serviceUrl=...</c> while the
    /// profiler treated the whole string as a service URL, so the same <c>--connection</c> the CLI passes to both
    /// commands worked for one and failed for the other. Nothing caught it because neither had ever connected.
    /// </summary>
    [Fact]
    public async Task DynamoDb_DiscovererAndProfilerAcceptTheSameConnectionString()
    {
        if (fixture.DynamoConnectionString is null) return;

        var collections = await new DynamoDbSchemaDiscoverer()
            .DiscoverCollectionsAsync(fixture.DynamoConnectionString, Collection);

        var act = async () => await new DynamoDbDataProfiler()
            .ProfileAsync(collections, fixture.DynamoConnectionString);

        await act.Should().NotThrowAsync();
    }

    // ── Firestore ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Firestore_DiscoversTheCollectionAndItsFields()
    {
        if (fixture.FirestoreProjectId is null) return;

        var collections = await new FirestoreSchemaDiscoverer()
            .DiscoverCollectionsAsync(fixture.FirestoreProjectId, string.Empty);

        var collection = collections.Should().ContainSingle(c => c.CollectionName == Collection).Subject;
        collection.Fields.Select(f => f.Name).Should().Contain(["email", "age", "active", "note"]);
        collection.PartitionKey.Should().BeNull("Firestore manages partitioning internally");

        foreach (var field in collection.Fields) NoSqlProfilerTests.AssertValidFieldDescriptor(field);
    }

    [Fact]
    public async Task Firestore_ProfilesAggregatesWithoutDocumentContent()
    {
        if (fixture.FirestoreProjectId is null) return;

        var snapshot = await ProfileAsync(
            new FirestoreSchemaDiscoverer(), new FirestoreDataProfiler(), fixture.FirestoreProjectId, string.Empty);

        AssertSharedProfileShape(snapshot, "firestore");
    }

    // ── Cosmos DB, opt-in ────────────────────────────────────────────────────────

    /// <summary>
    /// The Cosmos emulator is a multi-gigabyte image that takes minutes to become healthy, so it is opt-in via
    /// <c>FABRICATE_COSMOS_EMULATOR=1</c> rather than run on every CI job. Its absence is stated by
    /// <see cref="NoSqlEmulatorFixture.Report"/> rather than left to be inferred from a green run.
    /// </summary>
    [Fact]
    public async Task CosmosDb_ProfilesAggregatesWithoutDocumentContent()
    {
        if (fixture.CosmosConnectionString is null)
        {
            output.WriteLine(fixture.CosmosSkipReason);
            return;
        }

        var snapshot = await ProfileAsync(
            new CosmosDbSchemaDiscoverer(), new CosmosDbDataProfiler(),
            fixture.CosmosConnectionString, NoSqlEmulatorFixture.CosmosDatabase);

        AssertSharedProfileShape(snapshot, "cosmosdb");
    }

    [Fact]
    public async Task CosmosDb_DiscoversTheContainerAndItsPartitionKey()
    {
        if (fixture.CosmosConnectionString is null)
        {
            output.WriteLine(fixture.CosmosSkipReason);
            return;
        }

        var collections = await new CosmosDbSchemaDiscoverer()
            .DiscoverCollectionsAsync(fixture.CosmosConnectionString, NoSqlEmulatorFixture.CosmosDatabase);

        var container = collections.Should().ContainSingle(c => c.CollectionName == Collection).Subject;
        container.DatabaseName.Should().Be(NoSqlEmulatorFixture.CosmosDatabase);
        container.PartitionKey.Should().NotBeNull("every Cosmos container is created with one");

        foreach (var field in container.Fields) NoSqlProfilerTests.AssertValidFieldDescriptor(field);
    }

    // ── shared assertions ────────────────────────────────────────────────────────

    private static async Task<NoSqlProfileSnapshot> ProfileAsync(
        INoSqlSchemaDiscoverer discoverer,
        INoSqlDataProfiler profiler,
        string connectionString,
        string databaseName)
    {
        var collections = await discoverer.DiscoverCollectionsAsync(connectionString, databaseName);
        return await profiler.ProfileAsync(collections, connectionString);
    }

    /// <summary>
    /// What the MongoDB suite asserts, applied to every provider. Providers qualify a collection name differently
    /// — DynamoDB has no database, Firestore names one per project — so the collection is matched by suffix and
    /// everything else is asserted on the field, which is the part that must agree.
    /// </summary>
    private static void AssertSharedProfileShape(NoSqlProfileSnapshot snapshot, string provider)
    {
        snapshot.ProviderName.Should().Be(provider);

        var collection = snapshot.Collections
            .Should().ContainSingle(c => c.QualifiedName.EndsWith(Collection, StringComparison.Ordinal)).Subject;

        collection.DocumentCount.Should().Be(3);

        var age = collection.FieldProfiles.Should().ContainSingle(f => f.FieldPath == "age").Subject;
        age.InferredType.Should().Be(DocumentFieldType.Number);
        age.NonNullCount.Should().Be(3);
        age.NullCount.Should().Be(0);
        age.ApproximateDistinctValues.Should().Be(3);
        string.CompareOrdinal(age.MinValue, age.MaxValue).Should().BeLessThan(0,
            "numeric min and max are stored zero-padded so ordinal comparison matches numeric order");

        var active = collection.FieldProfiles.Should().ContainSingle(f => f.FieldPath == "active").Subject;
        active.InferredType.Should().Be(DocumentFieldType.Boolean);
        active.ApproximateDistinctValues.Should().Be(2);

        // note: carried by one document, explicitly null on a second, absent from a third.
        var note = collection.FieldProfiles.Should().ContainSingle(f => f.FieldPath == "note").Subject;
        note.NonNullCount.Should().Be(1);
        note.NullCount.Should().Be(2,
            "a document that does not carry the field counts the same as one where the field is null");

        collection.FieldProfiles.Should().Contain(f => f.FieldPath == "address.city",
            "a nested object is profiled by path");

        var tags = collection.FieldProfiles.Should().ContainSingle(f => f.FieldPath == "tags").Subject;
        tags.InferredType.Should().Be(DocumentFieldType.Array);

        // A string field's min/max is the length range, never the value — otherwise a free-text field with one
        // entry reports that entry twice, and the profile becomes a second copy of the data (#71, #83).
        var email = collection.FieldProfiles.Should().ContainSingle(f => f.FieldPath == "email").Subject;
        email.MinValue.Should().NotContain("@");
        email.MaxValue.Should().NotContain("@");

        var serialised = JsonSerializer.Serialize(snapshot);
        serialised.Should().NotContain(NoSqlEmulatorFixture.SecretNote,
            "a profiler that carries document content is a second copy of the data");
        serialised.Should().NotContain(NoSqlEmulatorFixture.SecretCity);
        serialised.Should().NotContain("patient.zero");
        serialised.Should().NotContain("second@example.invalid");
    }
}

/// <summary>
/// Starts DynamoDB Local and the Firestore emulator once for the suite, and Cosmos too when
/// <c>FABRICATE_COSMOS_EMULATOR=1</c>. Seeds all three with the same documents.
/// </summary>
public sealed class NoSqlEmulatorFixture : IAsyncLifetime
{
    /// <summary>Planted in the documents; none of it may appear in a profile snapshot.</summary>
    internal const string SecretNote = "DIAGNOSIS-CONFIDENTIAL-DO-NOT-PROFILE";
    internal const string SecretCity = "Leeds";

    internal const string Collection = "patients";
    internal const string CosmosDatabase = "fabricate";
    private const string FirestoreProject = "fabricate-emulator";

    /// <summary>The emulator's published, non-secret key — the same one Microsoft's own documentation prints.</summary>
    private const string CosmosEmulatorKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private DynamoDbContainer? _dynamo;
    private IContainer? _firestore;
    private IContainer? _cosmos;

    private string? _originalAccessKey;
    private string? _originalSecretKey;
    private string? _originalEmulatorHost;

    public bool DockerAvailable { get; private set; }

    public string? DynamoConnectionString { get; private set; }
    public string? FirestoreProjectId { get; private set; }
    public string? CosmosConnectionString { get; private set; }

    public string? DynamoFailure { get; private set; }
    public string? FirestoreFailure { get; private set; }
    public string? CosmosFailure { get; private set; }

    /// <summary>
    /// Why the Cosmos tests did nothing. "Not asked for" and "asked for and broken" are different facts, and a
    /// run that prints the first when the second is true is the silent-skip failure this issue exists to correct.
    /// </summary>
    public string CosmosSkipReason =>
        CosmosFailure is not null
            ? $"Cosmos DB was requested but its emulator did not start: {Summarise(CosmosFailure)}"
            : "Cosmos DB NOT exercised: set FABRICATE_COSMOS_EMULATOR=1 to run it.";

    /// <summary>What actually ran, so a skipped provider is stated rather than inferred from a green suite (#91).</summary>
    public string Report()
    {
        static string State(string? connection, string? failure, string skipped)
            => connection is not null ? "EXERCISED" : failure is not null ? $"FAILED: {Summarise(failure)}" : skipped;

        return $"""
            NoSQL emulator coverage (#91)
              dynamodb  {State(DynamoConnectionString, DynamoFailure, "not run (no Docker)")}
              firestore {State(FirestoreProjectId, FirestoreFailure, "not run (no Docker)")}
              cosmosdb  {State(CosmosConnectionString, CosmosFailure, "not run (set FABRICATE_COSMOS_EMULATOR=1)")}
              mongodb   covered separately by NoSqlProfilerTests
            """;
    }

    private static string Summarise(string failure) => failure.Split('\n')[0].Trim();

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("FABRICATE_SKIP_DOCKER_TESTS") == "1") return;

        await StartDynamoAsync();
        await StartFirestoreAsync();

        if (Environment.GetEnvironmentVariable("FABRICATE_COSMOS_EMULATOR") == "1") await StartCosmosAsync();

        DockerAvailable = DynamoConnectionString is not null || FirestoreProjectId is not null;
    }

    public async Task DisposeAsync()
    {
        if (_dynamo is not null) await _dynamo.DisposeAsync();
        if (_firestore is not null) await _firestore.DisposeAsync();
        if (_cosmos is not null) await _cosmos.DisposeAsync();

        Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", _originalAccessKey);
        Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", _originalSecretKey);
        Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", _originalEmulatorHost);
    }

    // ── DynamoDB Local ───────────────────────────────────────────────────────────

    private async Task StartDynamoAsync()
    {
        try
        {
            _dynamo = new DynamoDbBuilder("amazon/dynamodb-local:2.5.2").Build();
            await _dynamo.StartAsync();

            // DynamoDB Local accepts any credentials, but the adapters deliberately resolve them from the standard
            // chain rather than taking keys, so the chain is given something to find. This collection does not run
            // in parallel with anything else, which is what makes setting them process-wide safe.
            _originalAccessKey = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
            _originalSecretKey = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
            Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "emulator");
            Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "emulator");

            var connectionString = $"region=us-east-1;serviceUrl={_dynamo.GetConnectionString()}";

            // Seeded through the adapters' own connection-string parser, so the format is exercised on the way in
            // as well as on the way out.
            using var client = DynamoDbConnectionString.CreateClient(connectionString);
            await client.CreateTableAsync(new CreateTableRequest
            {
                TableName = Collection,
                KeySchema = [new KeySchemaElement("id", KeyType.HASH)],
                AttributeDefinitions = [new AttributeDefinition("id", ScalarAttributeType.S)],
                BillingMode = BillingMode.PAY_PER_REQUEST,
            });

            foreach (var item in DynamoItems()) await client.PutItemAsync(Collection, item);

            DynamoConnectionString = connectionString;
        }
        catch (Exception ex)
        {
            DynamoFailure = ex.ToString();
            DynamoConnectionString = null;
            if (_dynamo is not null) await _dynamo.DisposeAsync();
            _dynamo = null;
        }
    }

    private static List<Dictionary<string, AttributeValue>> DynamoItems() =>
    [
        new()
        {
            ["id"] = new AttributeValue { S = "1" },
            ["email"] = new AttributeValue { S = "patient.zero@example.invalid" },
            ["age"] = new AttributeValue { N = "41" },
            ["active"] = new AttributeValue { BOOL = true },
            ["note"] = new AttributeValue { S = SecretNote },
            ["address"] = new AttributeValue { M = new Dictionary<string, AttributeValue> { ["city"] = new() { S = SecretCity } } },
            ["tags"] = new AttributeValue { L = [new AttributeValue { S = "a" }, new AttributeValue { S = "b" }] },
        },
        new()
        {
            ["id"] = new AttributeValue { S = "2" },
            ["email"] = new AttributeValue { S = "second@example.invalid" },
            ["age"] = new AttributeValue { N = "29" },
            ["active"] = new AttributeValue { BOOL = false },
            ["note"] = new AttributeValue { NULL = true },
        },
        new()
        {
            ["id"] = new AttributeValue { S = "3" },
            ["email"] = new AttributeValue { S = "third@example.invalid" },
            ["age"] = new AttributeValue { N = "63" },
            ["active"] = new AttributeValue { BOOL = true },
        },
    ];

    // ── Firestore emulator ───────────────────────────────────────────────────────

    private async Task StartFirestoreAsync()
    {
        try
        {
            _firestore = new ContainerBuilder("gcr.io/google.com/cloudsdktool/google-cloud-cli:emulators")
                .WithPortBinding(8080, assignRandomHostPort: true)
                .WithCommand("/bin/bash", "-c",
                    $"gcloud emulators firestore start --project={FirestoreProject} --host-port=0.0.0.0:8080")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Dev App Server is now running"))
                .Build();

            await _firestore.StartAsync();

            // The Firestore client honours FIRESTORE_EMULATOR_HOST, so the production adapters reach the emulator
            // unchanged — unlike GCS, where the emulator needed a differently-built client (#90).
            _originalEmulatorHost = Environment.GetEnvironmentVariable("FIRESTORE_EMULATOR_HOST");
            Environment.SetEnvironmentVariable(
                "FIRESTORE_EMULATOR_HOST", $"{_firestore.Hostname}:{_firestore.GetMappedPublicPort(8080)}");

            // Seeded through the adapters' own client factory, so emulator detection is exercised on the way in
            // as well as on the way out.
            var database = await FirestoreConnection.CreateAsync(FirestoreProject);
            var collection = database.Collection(Collection);
            foreach (var (id, document) in FirestoreDocuments()) await collection.Document(id).SetAsync(document);

            FirestoreProjectId = FirestoreProject;
        }
        catch (Exception ex)
        {
            FirestoreFailure = ex.ToString();
            FirestoreProjectId = null;
            if (_firestore is not null) await _firestore.DisposeAsync();
            _firestore = null;
            Environment.SetEnvironmentVariable("FIRESTORE_EMULATOR_HOST", _originalEmulatorHost);
        }
    }

    private static IEnumerable<(string Id, Dictionary<string, object?> Document)> FirestoreDocuments()
    {
        yield return ("1", new Dictionary<string, object?>
        {
            ["email"] = "patient.zero@example.invalid",
            ["age"] = 41L,
            ["active"] = true,
            ["note"] = SecretNote,
            ["address"] = new Dictionary<string, object?> { ["city"] = SecretCity },
            ["tags"] = new List<object?> { "a", "b" },
        });

        yield return ("2", new Dictionary<string, object?>
        {
            ["email"] = "second@example.invalid",
            ["age"] = 29L,
            ["active"] = false,
            ["note"] = null,
        });

        yield return ("3", new Dictionary<string, object?>
        {
            ["email"] = "third@example.invalid",
            ["age"] = 63L,
            ["active"] = true,
        });
    }

    // ── Cosmos DB emulator, opt-in ───────────────────────────────────────────────

    private async Task StartCosmosAsync()
    {
        try
        {
            // vnext-preview rather than the older :latest emulator. That one writes its log to a file inside the
            // container and nothing to stdout, so no log-based wait strategy can match it, and it then spins
            // without ever serving on a WSL2 kernel. vnext is a third of the size and serves in seconds.
            //
            // No wait strategy either way: the container reports running long before it accepts a request, so
            // readiness is a real Cosmos call, below.
            _cosmos = new ContainerBuilder("mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview")
                .WithPortBinding(8081, assignRandomHostPort: true)
                .Build();

            await _cosmos.StartAsync();

            // Gateway mode and the certificate exemption are both emulator-only; CosmosConnectionString refuses
            // the latter outright for any endpoint that is not loopback.
            var endpoint = $"https://{_cosmos.Hostname}:{_cosmos.GetMappedPublicPort(8081)}/";
            var connectionString =
                $"AccountEndpoint={endpoint};AccountKey={CosmosEmulatorKey};" +
                "ConnectionMode=Gateway;DisableServerCertificateValidation=True;";

            using var client = Fabricate.Infrastructure.Schema.CosmosConnectionString.CreateClient(connectionString);

            // A Cosmos request through the production client is the only readiness definition that matters here.
            // Five minutes of patience, and then the failure is reported as one rather than swallowed.
            await SeedCosmosAsync(client, TimeSpan.FromMinutes(5));

            CosmosConnectionString = connectionString;
        }
        catch (Exception ex)
        {
            CosmosFailure = ex.ToString();
            CosmosConnectionString = null;
            if (_cosmos is not null) await _cosmos.DisposeAsync();
            _cosmos = null;
        }
    }

    private static async Task SeedCosmosAsync(Microsoft.Azure.Cosmos.CosmosClient client, TimeSpan patience)
    {
        var deadline = DateTimeOffset.UtcNow + patience;

        while (true)
        {
            try
            {
                var database = (await client.CreateDatabaseIfNotExistsAsync(CosmosDatabase)).Database;
                var container = (await database.CreateContainerIfNotExistsAsync(Collection, "/id")).Container;

                foreach (var document in CosmosDocuments())
                {
                    await container.UpsertItemAsync(
                        document, new Microsoft.Azure.Cosmos.PartitionKey((string)document["id"]!));
                }

                return;
            }
            catch (Exception) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static IEnumerable<Dictionary<string, object?>> CosmosDocuments()
    {
        yield return new Dictionary<string, object?>
        {
            ["id"] = "1",
            ["email"] = "patient.zero@example.invalid",
            ["age"] = 41,
            ["active"] = true,
            ["note"] = SecretNote,
            ["address"] = new Dictionary<string, object?> { ["city"] = SecretCity },
            ["tags"] = new[] { "a", "b" },
        };

        yield return new Dictionary<string, object?>
        {
            ["id"] = "2",
            ["email"] = "second@example.invalid",
            ["age"] = 29,
            ["active"] = false,
            ["note"] = null,
        };

        yield return new Dictionary<string, object?>
        {
            ["id"] = "3",
            ["email"] = "third@example.invalid",
            ["age"] = 63,
            ["active"] = true,
        };
    }
}

/// <summary>
/// These fixtures set process-wide environment variables — AWS credentials and the Firestore emulator host — which
/// nothing else may be reading at the same time.
/// </summary>
[CollectionDefinition("NoSqlEmulators", DisableParallelization = true)]
public sealed class NoSqlEmulatorCollection;
