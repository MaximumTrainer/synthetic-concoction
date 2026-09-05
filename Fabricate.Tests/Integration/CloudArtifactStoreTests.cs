using System.Security.Cryptography;
using System.Text;
using Azure.Storage.Blobs;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Fabricate.Application.Abstractions;
using Fabricate.Infrastructure.Export;
using FluentAssertions;
using Google.Apis.Storage.v1.Data;
using Google.Cloud.Storage.V1;
using Testcontainers.Azurite;

namespace Fabricate.Tests.Integration;

/// <summary>
/// #90: Azure Blob and GCS adapters, against emulators rather than cloud accounts — Azurite and
/// <c>fake-gcs-server</c> — so the suite stays runnable without credentials, which is what #84's split turned on.
///
/// <para>
/// Both suites make the same assertions the MinIO suite makes for S3, so a difference between adapters is a real
/// difference and not a difference of test.
/// </para>
/// </summary>
public sealed class CloudArtifactStoreTests : IAsyncLifetime
{
    private const string Bucket = "fabricate-artifacts";
    private const int LargeArtifactBytes = 24 * 1024 * 1024;

    internal static string? GcsStartupFailure;

    private AzuriteContainer? _azurite;
    private IContainer? _fakeGcs;
    private BlobContainerClient? _blobs;
    private StorageClient? _gcs;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("FABRICATE_SKIP_DOCKER_TESTS") == "1") return;

        await StartAzuriteAsync();
        await StartFakeGcsAsync();
    }

    private async Task StartAzuriteAsync()
    {
        try
        {
            _azurite = new AzuriteBuilder("mcr.microsoft.com/azure-storage/azurite:3.34.0").Build();
            await _azurite.StartAsync();

            // Azurite is reached with its well-known development connection string, which is a credential in
            // name only — the composition root's other path, managed identity, cannot be emulated.
            _blobs = CloudStorageClientFactory.CreateAzureBlob(
                new ArtifactStoreOptions
                {
                    Kind = "azure-blob",
                    BucketName = Bucket,
                    ConnectionStringSecretName = "azure",
                },
                new FixedSecrets(_azurite.GetConnectionString()));

            await _blobs.CreateIfNotExistsAsync();
        }
        catch (Exception)
        {
            _azurite = null;
            _blobs = null;
        }
    }

    private async Task StartFakeGcsAsync()
    {
        try
        {
            // A known host port, chosen before the container starts. fake-gcs-server puts its external URL into
            // the resumable-upload session it hands back, and with a random mapping it can only report its own
            // internal bind address — which the client then tries to connect to, and 0.0.0.0 is not a target.
            var hostPort = FreeTcpPort();
            var endpoint = $"http://localhost:{hostPort}";

            _fakeGcs = new ContainerBuilder("fsouza/fake-gcs-server:1.49")
                .WithPortBinding(hostPort, 4443)
                .WithCommand("-scheme", "http", "-port", "4443", "-public-host", $"localhost:{hostPort}",
                             "-external-url", endpoint)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(4443).ForPath("/storage/v1/b")))
                .Build();

            await _fakeGcs.StartAsync();

            Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", endpoint);

            // Pointed at the emulator explicitly, with authentication off. The production factory uses
            // Application Default Credentials, which an emulator cannot stand in for — so this builds the client
            // the one way the emulator accepts and exercises the *store* against it, not the credential path.
            _gcs = await new StorageClientBuilder
            {
                BaseUri = $"{endpoint}/storage/v1/",
                UnauthenticatedAccess = true,
            }.BuildAsync();

            await _gcs.CreateBucketAsync("fabricate-test", new Bucket { Name = Bucket });
        }
        catch (Exception ex)
        {
            GcsStartupFailure = ex.ToString();
            _gcs = null;
            if (_fakeGcs is not null) await _fakeGcs.DisposeAsync();
            _fakeGcs = null;
            Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", null);
        }
    }

    /// <summary>A port the OS says is free right now. Racy in principle; in practice the container binds it next.</summary>
    private static int FreeTcpPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task DisposeAsync()
    {
        _gcs?.Dispose();
        if (_azurite is not null) await _azurite.DisposeAsync();
        if (_fakeGcs is not null) await _fakeGcs.DisposeAsync();
        Environment.SetEnvironmentVariable("STORAGE_EMULATOR_HOST", null);
    }

    private AzureBlobArtifactStore? AzureStore() => _blobs is null ? null : new AzureBlobArtifactStore(_blobs);

    private GcsArtifactStore? GcsStore() => _gcs is null ? null : new GcsArtifactStore(_gcs, Bucket);

    private static Stream Content(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    // ── Azure Blob ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Azure_ArtifactsSurviveTheWritingProcess_AndVerifyAgainstTheirChecksum()
    {
        var store = AzureStore();
        if (store is null) return;

        var runId = Guid.NewGuid().ToString();
        const string body = "id,email\n1,a@example.com\n";

        var path = await store.StoreAsync(runId, "csv/main_users.csv", Content(body));

        // A second store instance — nothing carried over in memory from the write.
        var reader = new AzureBlobArtifactStore(_blobs!);

        var listed = await reader.ListAsync(runId);
        listed.Should().ContainSingle();
        listed[0].Name.Should().Be("csv/main_users.csv", "the exporter directory has to survive the round trip");
        listed[0].SizeBytes.Should().Be(Encoding.UTF8.GetByteCount(body));

        await using var downloaded = await reader.RetrieveAsync(path);
        using var buffer = new MemoryStream();
        await downloaded.CopyToAsync(buffer);

        Encoding.UTF8.GetString(buffer.ToArray()).Should().Be(body);
        Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()))
            .Should().Be(await reader.GetChecksumAsync(path),
                "the checksum stored with the blob must describe the bytes that come back");
    }

    [Fact]
    public async Task Azure_ALargeArtifactMovesBothWaysWithoutBeingHeldInMemory()
    {
        var store = AzureStore();
        if (store is null) return;

        await AssertStreamsAsync(
            store.StoreAsync,
            store.RetrieveAsync,
            store.ListAsync);
    }

    [Fact]
    public async Task Azure_RunsAreIsolatedAndTraversalCannotEscape()
    {
        var store = AzureStore();
        if (store is null) return;

        await AssertIsolationAsync(store.StoreAsync, store.ListAsync);
    }

    [Fact]
    public async Task Azure_DeletingARunRemovesEveryArtifact()
    {
        var store = AzureStore();
        if (store is null) return;

        var runId = await SeedThreeAsync(store.StoreAsync);

        (await store.DeleteRunAsync(runId)).Should().Be(3);
        (await store.ListAsync(runId)).Should().BeEmpty(
            "a purged run must report no artifacts rather than pointing at paths that no longer resolve");
    }

    [Fact]
    public async Task Azure_AMissingArtifactIsAFileNotFound()
    {
        var store = AzureStore();
        if (store is null) return;

        var missing = $"runs/{Guid.NewGuid()}/missing.csv";
        var retrieve = async () => await store.RetrieveAsync(missing);

        await retrieve.Should().ThrowAsync<FileNotFoundException>();
        (await store.ExistsAsync(missing)).Should().BeFalse();
    }

    // ── Google Cloud Storage ─────────────────────────────────────────────────────

    [Fact]
    public async Task Gcs_ArtifactsSurviveTheWritingProcess_AndVerifyAgainstTheirChecksum()
    {
        var store = GcsStore();
        if (store is null) return;

        var runId = Guid.NewGuid().ToString();
        const string body = "id,email\n1,a@example.com\n";

        var path = await store.StoreAsync(runId, "csv/main_users.csv", Content(body));
        var reader = new GcsArtifactStore(_gcs!, Bucket);

        var listed = await reader.ListAsync(runId);
        listed.Should().ContainSingle();
        listed[0].Name.Should().Be("csv/main_users.csv");
        listed[0].SizeBytes.Should().Be(Encoding.UTF8.GetByteCount(body));

        await using var downloaded = await reader.RetrieveAsync(path);
        using var buffer = new MemoryStream();
        await downloaded.CopyToAsync(buffer);

        Encoding.UTF8.GetString(buffer.ToArray()).Should().Be(body);
        Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()))
            .Should().Be(await reader.GetChecksumAsync(path));
    }

    [Fact]
    public async Task Gcs_ALargeArtifactMovesBothWaysWithoutBeingHeldInMemory()
    {
        var store = GcsStore();
        if (store is null) return;

        await AssertStreamsAsync(store.StoreAsync, store.RetrieveAsync, store.ListAsync);
    }

    [Fact]
    public async Task Gcs_RunsAreIsolatedAndTraversalCannotEscape()
    {
        var store = GcsStore();
        if (store is null) return;

        await AssertIsolationAsync(store.StoreAsync, store.ListAsync);
    }

    [Fact]
    public async Task Gcs_DeletingARunRemovesEveryArtifact()
    {
        var store = GcsStore();
        if (store is null) return;

        var runId = await SeedThreeAsync(store.StoreAsync);

        (await store.DeleteRunAsync(runId)).Should().Be(3);
        (await store.ListAsync(runId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Gcs_AMissingArtifactIsAFileNotFound()
    {
        var store = GcsStore();
        if (store is null) return;

        var missing = $"runs/{Guid.NewGuid()}/missing.csv";
        var retrieve = async () => await store.RetrieveAsync(missing);

        await retrieve.Should().ThrowAsync<FileNotFoundException>();
        (await store.ExistsAsync(missing)).Should().BeFalse();
    }

    /// <summary>
    /// The failure mode these suites are most exposed to is passing because nothing ran. Every test above
    /// self-skips when its emulator did not start, so without this a broken image reference or a renamed
    /// container flag would show as a green run with no coverage at all.
    /// </summary>
    [Fact]
    public void BothEmulatorsStartedWhenDockerIsAvailable()
    {
        if (Environment.GetEnvironmentVariable("FABRICATE_SKIP_DOCKER_TESTS") == "1") return;
        if (_azurite is null && _fakeGcs is null) return; // No Docker at all: the whole suite is out.

        _blobs.Should().NotBeNull("Azurite started, so the Azure Blob suite must actually have run");
        _gcs.Should().NotBeNull($"fake-gcs-server must start; it failed with: {GcsStartupFailure}");
    }

    // ── configuration ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("azure-blob")]
    [InlineData("gcs")]
    public void ObjectStorageWithoutABucketIsRefusedAtStartup(string kind)
    {
        var errors = new ArtifactStoreOptions { Kind = kind, AccountUrl = "https://acme.blob.core.windows.net" }.Validate();

        errors.Should().Contain(e => e.Contains("FABRICATE_ARTIFACT_BUCKET", StringComparison.Ordinal),
            "a misconfigured store must stop the instance starting, not be found by the first person to generate data");
    }

    [Fact]
    public void AzureWithNeitherAccountUrlNorConnectionStringIsRefused()
    {
        var errors = new ArtifactStoreOptions { Kind = "azure-blob", BucketName = Bucket }.Validate();

        errors.Should().Contain(e => e.Contains("FABRICATE_ARTIFACT_AZURE_ACCOUNT_URL", StringComparison.Ordinal));
    }

    [Fact]
    public void AmbientIdentityIsTheDefaultForBothClouds()
    {
        var azure = new ArtifactStoreOptions
        {
            Kind = "azure-blob", BucketName = Bucket, AccountUrl = "https://acme.blob.core.windows.net",
        };
        var gcs = new ArtifactStoreOptions { Kind = "gcs", BucketName = Bucket };

        azure.Validate().Should().BeEmpty("managed identity needs no stored credential");
        gcs.Validate().Should().BeEmpty("Application Default Credentials need no stored credential");
        azure.ConnectionStringSecretName.Should().BeNull();
        gcs.CredentialsJsonSecretName.Should().BeNull();
    }

    [Fact]
    public void AnUnknownStoreKindIsRefusedAndNamesTheOnesThatWork()
    {
        var errors = new ArtifactStoreOptions { Kind = "dropbox" }.Validate();

        errors.Should().Contain(e => e.Contains("'filesystem', 's3', 'azure-blob' or 'gcs'", StringComparison.Ordinal));
    }

    // ── shared assertions ────────────────────────────────────────────────────────

    private delegate Task<string> StoreAsync(string runId, string name, Stream content, CancellationToken cancellationToken = default);
    private delegate Task<Stream> RetrieveAsync(string path, CancellationToken cancellationToken = default);
    private delegate Task<IReadOnlyList<StoredArtifact>> ListAsync(string runId, CancellationToken cancellationToken = default);

    private static async Task AssertStreamsAsync(StoreAsync store, RetrieveAsync retrieve, ListAsync list)
    {
        var runId = Guid.NewGuid().ToString();

        var before = LiveBytes();
        var path = await store(runId, "csv/large.csv", new GeneratedStream(LargeArtifactBytes));

        await using (var download = await retrieve(path))
        {
            var buffer = new byte[81920];
            long read = 0;
            int chunk;
            while ((chunk = await download.ReadAsync(buffer)) > 0) read += chunk;
            read.Should().Be(LargeArtifactBytes);
        }

        var growth = LiveBytes() - before;
        growth.Should().BeLessThan(LargeArtifactBytes / 2,
            $"streaming must not materialise the artifact; live heap grew by {growth / 1024 / 1024} MB for a " +
            $"{LargeArtifactBytes / 1024 / 1024} MB file");

        (await list(runId))[0].SizeBytes.Should().Be(LargeArtifactBytes);
    }

    private static async Task AssertIsolationAsync(StoreAsync store, ListAsync list)
    {
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();

        await store(first, "csv/a.csv", Content("a"));
        await store(second, "csv/b.csv", Content("b"));

        (await list(first)).Should().ContainSingle().Which.Name.Should().Be("csv/a.csv");
        (await list(second)).Should().ContainSingle().Which.Name.Should().Be("csv/b.csv");
        (await list(Guid.NewGuid().ToString())).Should().BeEmpty();

        // Traversal segments are dropped, so the key lands inside the run's own prefix.
        var traversal = Guid.NewGuid().ToString();
        var path = await store(traversal, "../../etc/passwd", Content("nope"));

        path.Should().Be($"runs/{traversal}/etc/passwd");
        path.Should().NotContain("..");
    }

    private static async Task<string> SeedThreeAsync(StoreAsync store)
    {
        var runId = Guid.NewGuid().ToString();
        await store(runId, "csv/a.csv", Content("a"));
        await store(runId, "json/a.json", Content("{}"));
        await store(runId, "summary.json", Content("{}"));
        return runId;
    }

    private static long LiveBytes()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    /// <summary>Produces N bytes on demand, so the test never holds the payload it is checking is not held.</summary>
    private sealed class GeneratedStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - _position;
            if (remaining <= 0) return 0;

            var toWrite = (int)Math.Min(count, remaining);
            Array.Fill(buffer, (byte)'x', offset, toWrite);
            _position += toWrite;
            return toWrite;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FixedSecrets(string value) : ISecretProvider
    {
        public Task<string> ResolveAsync(string secretName, CancellationToken cancellationToken = default)
            => Task.FromResult(value);

        public Task<bool> ExistsAsync(string secretName, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}
