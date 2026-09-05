using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Fabricate.Application.Abstractions;
using Fabricate.Infrastructure.Export;
using FluentAssertions;
using Testcontainers.Minio;

namespace Fabricate.Tests.Integration;

/// <summary>
/// #84: the local file system is ephemeral on every hosted target, so a completed run pointed at files that no
/// longer existed. These exercise the S3-compatible adapter against a real MinIO — the same API AWS S3,
/// Cloudflare R2 and Backblaze B2 speak — including the two properties that are easy to claim and hard to hold:
/// that the artifact survives the writing process, and that neither upload nor download buffers it whole.
/// </summary>
public sealed class S3ArtifactStoreTests : IAsyncLifetime
{
    private const string Bucket = "fabricate-artifacts";

    private MinioContainer? _container;
    private IAmazonS3? _client;

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("FABRICATE_SKIP_DOCKER_TESTS") == "1") return;

        try
        {
            _container = new MinioBuilder("minio/minio:RELEASE.2025-04-22T22-12-26Z").Build();
            await _container.StartAsync();

            _client = S3ClientFactory.Create(
                new ArtifactStoreOptions
                {
                    Kind = "s3",
                    BucketName = Bucket,
                    ServiceUrl = _container.GetConnectionString(),
                    ForcePathStyle = true,
                    Region = "us-east-1",
                    AccessKeySecretName = "access",
                    SecretKeySecretName = "secret",
                },
                new FixedSecrets(_container.GetAccessKey(), _container.GetSecretKey()));

            await _client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
        }
        catch (Exception)
        {
            // No Docker: every test below becomes a no-op, as the PostgreSQL suites do.
            _container = null;
            _client = null;
        }
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_container is not null) await _container.DisposeAsync();
    }

    private S3ArtifactStore? NewStore() => _client is null ? null : new S3ArtifactStore(_client, Bucket);

    private static Stream Content(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task ArtifactsSurviveTheWritingProcess_AndVerifyAgainstTheirChecksum()
    {
        var store = NewStore();
        if (store is null) return;

        var runId = Guid.NewGuid().ToString();
        const string body = "id,email\n1,a@example.com\n";

        var path = await store.StoreAsync(runId, "csv/main_users.csv", Content(body));

        // A second store instance — nothing carried over in memory from the write.
        var reader = new S3ArtifactStore(_client!, Bucket);

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
                "the checksum stored with the object must describe the bytes that come back");
    }

    [Fact]
    public async Task TheChecksumIsRecordedAtUploadWithoutAReReadOfTheObject()
    {
        var store = NewStore();
        if (store is null) return;

        var runId = Guid.NewGuid().ToString();
        const string body = "hello object storage";
        var path = await store.StoreAsync(runId, "summary.json", Content(body));

        var stored = await store.GetChecksumAsync(path);

        stored.Should().Be(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))),
            "the manifest is served from metadata so it never has to read the blobs (#66)");
    }

    [Fact]
    public async Task ALargeArtifactUploadsAndDownloadsWithoutBeingHeldInMemory()
    {
        var store = NewStore();
        if (store is null) return;

        // 24 MB, produced and consumed a chunk at a time. Held whole at any point this would show up as a
        // multiple of the payload; the assertion is on live heap growth, not on total allocation.
        const int totalBytes = 24 * 1024 * 1024;
        var runId = Guid.NewGuid().ToString();

        var before = LiveBytes();
        var path = await store.StoreAsync(runId, "csv/large.csv", new GeneratedStream(totalBytes));

        await using (var download = await store.RetrieveAsync(path))
        {
            var buffer = new byte[81920];
            long read = 0;
            int chunk;
            while ((chunk = await download.ReadAsync(buffer)) > 0) read += chunk;
            read.Should().Be(totalBytes);
        }

        var growth = LiveBytes() - before;
        growth.Should().BeLessThan(totalBytes / 2,
            $"streaming must not materialise the artifact; live heap grew by {growth / 1024 / 1024} MB for a " +
            $"{totalBytes / 1024 / 1024} MB file");

        (await store.ListAsync(runId))[0].SizeBytes.Should().Be(totalBytes);
    }

    [Fact]
    public async Task ArtifactsOfOneRunAreNotListedUnderAnother()
    {
        var store = NewStore();
        if (store is null) return;

        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();

        await store.StoreAsync(first, "csv/a.csv", Content("a"));
        await store.StoreAsync(second, "csv/b.csv", Content("b"));

        (await store.ListAsync(first)).Should().ContainSingle().Which.Name.Should().Be("csv/a.csv");
        (await store.ListAsync(second)).Should().ContainSingle().Which.Name.Should().Be("csv/b.csv");
        (await store.ListAsync(Guid.NewGuid().ToString())).Should().BeEmpty();
    }

    [Fact]
    public async Task ANameCannotEscapeItsRunPrefix()
    {
        var store = NewStore();
        if (store is null) return;

        var runId = Guid.NewGuid().ToString();

        var path = await store.StoreAsync(runId, "../../etc/passwd", Content("nope"));

        // The traversal segments are dropped and the rest is kept, so the key lands inside the run's own prefix.
        // What matters is the prefix, not that the remaining name looks unusual.
        path.Should().Be($"runs/{runId}/etc/passwd");
        path.Should().NotContain("..");
        (await store.ListAsync(runId)).Should().ContainSingle().Which.Name.Should().Be("etc/passwd");
        (await store.ListAsync(Guid.NewGuid().ToString())).Should().BeEmpty(
            "nothing was written outside the run it was stored under");
    }

    [Fact]
    public async Task DeletingARunRemovesEveryArtifact()
    {
        var store = NewStore();
        if (store is null) return;

        var runId = Guid.NewGuid().ToString();
        await store.StoreAsync(runId, "csv/a.csv", Content("a"));
        await store.StoreAsync(runId, "json/a.json", Content("{}"));
        await store.StoreAsync(runId, "summary.json", Content("{}"));

        var removed = await store.DeleteRunAsync(runId);

        removed.Should().Be(3);
        (await store.ListAsync(runId)).Should().BeEmpty(
            "a purged run must report no artifacts rather than pointing at paths that no longer resolve");
    }

    [Fact]
    public async Task AMissingArtifactIsAFileNotFound()
    {
        var store = NewStore();
        if (store is null) return;

        var retrieve = async () => await store.RetrieveAsync($"runs/{Guid.NewGuid()}/missing.csv");

        await retrieve.Should().ThrowAsync<FileNotFoundException>();
        (await store.ExistsAsync($"runs/{Guid.NewGuid()}/missing.csv")).Should().BeFalse();
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

    private sealed class FixedSecrets(string accessKey, string secretKey) : ISecretProvider
    {
        public Task<string> ResolveAsync(string secretName, CancellationToken cancellationToken = default)
            => Task.FromResult(secretName == "access" ? accessKey : secretKey);

        public Task<bool> ExistsAsync(string secretName, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}
