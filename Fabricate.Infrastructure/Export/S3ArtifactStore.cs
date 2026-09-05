using Amazon.S3;
using Amazon.S3.Model;
using Fabricate.Application.Abstractions;

namespace Fabricate.Infrastructure.Export;

/// <summary>
/// Stores artifacts in any S3-compatible object store (#84).
///
/// <para>
/// The local file system is ephemeral on every hosted target — Fly machines are replaced, Cloud Run and Container
/// Apps revisions are immutable, ECS tasks restart — so a completed run pointed at files that no longer existed.
/// One API covers AWS S3, MinIO, Cloudflare R2 and Backblaze B2, which is why it is the adapter self-hosters and
/// cloud deployments share.
/// </para>
///
/// <para>
/// Uploads and downloads stream. Buffering a whole artifact to compute its length or its checksum would undo the
/// streaming export work (#82), so the size and SHA-256 are computed <em>as the bytes pass through</em> and then
/// stored as object metadata — which is also what lets the run manifest (#66) be served without reading the blobs.
/// </para>
/// </summary>
public sealed class S3ArtifactStore(IAmazonS3 client, string bucketName) : IArtifactStore
{
    /// <summary>User metadata keys. S3 lowercases and prefixes these, so they are read back case-insensitively.</summary>
    private const string ChecksumKey = "sha256";
    private const string ContentLengthKey = "content-length-bytes";

    public async Task<string> StoreAsync(string runId, string name, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var key = KeyFor(runId, name);

        // The SDK needs a seekable stream to sign the payload — payload signing cannot be skipped over plain
        // HTTP, which is how MinIO and most self-hosted stores are reached — and a checksum has to be computed
        // over the whole body regardless. Hashing while spooling to a temp file keeps peak memory at one buffer
        // rather than one artifact; the streaming export can produce files far larger than it is safe to hold.
        var spool = new TempFileStream();
        try
        {
            var (length, checksum) = await spool.FillAsync(content, cancellationToken).ConfigureAwait(false);

            var request = new PutObjectRequest
            {
                BucketName = bucketName,
                Key = key,
                InputStream = spool.Stream,
                AutoCloseStream = false,
            };

            request.Metadata.Add(ChecksumKey, checksum);
            request.Metadata.Add(ContentLengthKey, length.ToString(System.Globalization.CultureInfo.InvariantCulture));

            await client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return key;
        }
        finally
        {
            await spool.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<Stream> RetrieveAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await client.GetObjectAsync(bucketName, path, cancellationToken).ConfigureAwait(false);

            // The response stream is the network stream: the caller reads it straight through to their own output.
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Artifact not found at '{path}'.", path, ex);
        }
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.GetObjectMetadataAsync(bucketName, path, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<StoredArtifact>> ListAsync(string runId, CancellationToken cancellationToken = default)
    {
        var prefix = Prefix(runId);
        var artifacts = new List<StoredArtifact>();
        string? continuationToken = null;

        do
        {
            var response = await client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = bucketName, Prefix = prefix, ContinuationToken = continuationToken },
                cancellationToken).ConfigureAwait(false);

            foreach (var obj in response.S3Objects ?? [])
            {
                artifacts.Add(new StoredArtifact(obj.Key[prefix.Length..], obj.Key, obj.Size ?? 0));
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);

        return artifacts.OrderBy(a => a.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The SHA-256 recorded when the object was written, or null when it carries none.</summary>
    public async Task<string?> GetChecksumAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = await client.GetObjectMetadataAsync(bucketName, path, cancellationToken).ConfigureAwait(false);
            return metadata.Metadata[ChecksumKey];
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes every object for a run. Used by the retention pass where the bucket has no lifecycle policy.
    /// </summary>
    public async Task<int> DeleteRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        var artifacts = await ListAsync(runId, cancellationToken).ConfigureAwait(false);
        if (artifacts.Count == 0) return 0;

        // One batch call rather than one request per object: a run can have hundreds of files.
        foreach (var batch in artifacts.Chunk(1000))
        {
            await client.DeleteObjectsAsync(
                new DeleteObjectsRequest
                {
                    BucketName = bucketName,
                    Objects = batch.Select(a => new KeyVersion { Key = a.Path }).ToList(),
                },
                cancellationToken).ConfigureAwait(false);
        }

        return artifacts.Count;
    }

    private static string Prefix(string runId) => $"runs/{Path.GetFileName(runId)}/";

    private static string KeyFor(string runId, string name)
    {
        // Names carry an exporter directory (csv/main_users.csv). Segments are sanitised individually so the
        // structure survives while traversal cannot escape the run's prefix.
        var segments = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileName)
            .Where(seg => !string.IsNullOrEmpty(seg) && seg != "." && seg != "..")
            .ToArray();

        if (segments.Length == 0) throw new ArgumentException("Artifact name resolves to nothing.", nameof(name));

        return Prefix(runId) + string.Join('/', segments);
    }

    /// <summary>
    /// A temp file the upload body is spooled through, hashing as it goes. Deleted on dispose by the file
    /// system itself (DeleteOnClose), so a crashed upload does not leave the artifact behind on disk.
    /// </summary>
    private sealed class TempFileStream : IAsyncDisposable
    {
        internal FileStream Stream { get; } = new(
            Path.Combine(Path.GetTempPath(), $"fabricate-upload-{Guid.NewGuid():N}"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        internal async Task<(long Length, string Checksum)> FillAsync(Stream source, CancellationToken cancellationToken)
        {
            using var hasher = System.Security.Cryptography.SHA256.Create();
            var buffer = new byte[81920];
            long length = 0;

            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hasher.TransformBlock(buffer, 0, read, null, 0);
                await Stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                length += read;
            }

            hasher.TransformFinalBlock([], 0, 0);
            await Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            Stream.Position = 0;

            return (length, Convert.ToHexStringLower(hasher.Hash!));
        }

        public ValueTask DisposeAsync() => Stream.DisposeAsync();
    }
}
