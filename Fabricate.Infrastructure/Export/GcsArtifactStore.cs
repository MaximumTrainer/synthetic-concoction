using System.Globalization;
using Fabricate.Application.Abstractions;
using Google;
using Google.Cloud.Storage.V1;

namespace Fabricate.Infrastructure.Export;

/// <summary>
/// Stores artifacts in Google Cloud Storage (#90).
///
/// <para>
/// GCS has an S3-compatible interoperability mode, so a GCP deployment could point the S3 adapter at it — but
/// that mode needs HMAC keys, which is exactly the stored credential Application Default Credentials exists to
/// avoid. The native client uses ADC, so on GKE or Cloud Run no key is stored anywhere.
/// </para>
/// </summary>
public sealed class GcsArtifactStore(StorageClient client, string bucketName) : IArtifactStore
{
    private const string ChecksumKey = "sha256";
    private const string ContentLengthKey = "contentlengthbytes";

    public async Task<string> StoreAsync(string runId, string name, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var key = ArtifactKey.For(runId, name);
        await using var spool = await ArtifactUploadSpool.FillAsync(content, cancellationToken).ConfigureAwait(false);

        var obj = new Google.Apis.Storage.v1.Data.Object
        {
            Bucket = bucketName,
            Name = key,
            ContentType = "application/octet-stream",
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ChecksumKey] = spool.Checksum,
                [ContentLengthKey] = spool.Length.ToString(CultureInfo.InvariantCulture),
            },
        };

        await client.UploadObjectAsync(obj, spool.Stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return key;
    }

    public async Task<Stream> RetrieveAsync(string path, CancellationToken cancellationToken = default)
    {
        // The client writes into a stream rather than handing one back, so the download is spooled to a temp
        // file the caller reads from — the same trade the upload makes, and for the same reason: never hold a
        // whole artifact in memory.
        var spool = new FileStream(
            Path.Combine(Path.GetTempPath(), $"fabricate-download-{Guid.NewGuid():N}"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        try
        {
            await client.DownloadObjectAsync(bucketName, path, spool, cancellationToken: cancellationToken).ConfigureAwait(false);
            spool.Position = 0;
            return spool;
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await spool.DisposeAsync().ConfigureAwait(false);
            throw new FileNotFoundException($"Artifact not found at '{path}'.", path, ex);
        }
        catch
        {
            await spool.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            await client.GetObjectAsync(bucketName, path, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<StoredArtifact>> ListAsync(string runId, CancellationToken cancellationToken = default)
    {
        var prefix = ArtifactKey.Prefix(runId);
        var artifacts = new List<StoredArtifact>();

        await foreach (var obj in client.ListObjectsAsync(bucketName, prefix).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            artifacts.Add(new StoredArtifact(obj.Name[prefix.Length..], obj.Name, (long)(obj.Size ?? 0)));
        }

        return artifacts.OrderBy(a => a.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The SHA-256 recorded when the object was written, or null when it carries none.</summary>
    public async Task<string?> GetChecksumAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var obj = await client.GetObjectAsync(bucketName, path, cancellationToken: cancellationToken).ConfigureAwait(false);
            return obj.Metadata is not null && obj.Metadata.TryGetValue(ChecksumKey, out var checksum) ? checksum : null;
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Deletes every object for a run, for the retention sweep where the bucket has no lifecycle rule.</summary>
    public async Task<int> DeleteRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        var artifacts = await ListAsync(runId, cancellationToken).ConfigureAwait(false);

        foreach (var artifact in artifacts)
        {
            try
            {
                await client.DeleteObjectAsync(bucketName, artifact.Path, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Already gone — a lifecycle rule and this sweep can both be running.
            }
        }

        return artifacts.Count;
    }
}
