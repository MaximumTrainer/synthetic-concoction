using System.Globalization;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Fabricate.Application.Abstractions;

namespace Fabricate.Infrastructure.Export;

/// <summary>
/// Stores artifacts in Azure Blob Storage (#90).
///
/// <para>
/// An Azure deployment can already point the S3 adapter at an S3-compatible endpoint, but that is a workaround:
/// on Azure the native store is the one with managed identity, lifecycle policies and the billing relationship
/// already in place.
/// </para>
///
/// <para>
/// Uploads and downloads stream, and the size and SHA-256 are computed as the bytes pass through and stored as
/// blob metadata — which is what lets the run manifest (#66) be served without reading the blobs.
/// </para>
/// </summary>
public sealed class AzureBlobArtifactStore(BlobContainerClient container) : IArtifactStore
{
    /// <summary>Metadata keys. Azure preserves the case it is given but matches case-insensitively on read.</summary>
    private const string ChecksumKey = "sha256";
    private const string ContentLengthKey = "contentlengthbytes";

    public async Task<string> StoreAsync(string runId, string name, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var key = ArtifactKey.For(runId, name);
        await using var spool = await ArtifactUploadSpool.FillAsync(content, cancellationToken).ConfigureAwait(false);

        await container.GetBlobClient(key).UploadAsync(
            spool.Stream,
            new BlobUploadOptions
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ChecksumKey] = spool.Checksum,
                    [ContentLengthKey] = spool.Length.ToString(CultureInfo.InvariantCulture),
                },
            },
            cancellationToken).ConfigureAwait(false);

        return key;
    }

    public async Task<Stream> RetrieveAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            // OpenReadAsync gives a stream that pulls from the service as the caller reads, rather than
            // downloading the blob first.
            return await container.GetBlobClient(path).OpenReadAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new FileNotFoundException($"Artifact not found at '{path}'.", path, ex);
        }
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
        => await container.GetBlobClient(path).ExistsAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<StoredArtifact>> ListAsync(string runId, CancellationToken cancellationToken = default)
    {
        var prefix = ArtifactKey.Prefix(runId);
        var artifacts = new List<StoredArtifact>();

        await foreach (var blob in container.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            artifacts.Add(new StoredArtifact(blob.Name[prefix.Length..], blob.Name, blob.Properties.ContentLength ?? 0));
        }

        return artifacts.OrderBy(a => a.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>The SHA-256 recorded when the blob was written, or null when it carries none.</summary>
    public async Task<string?> GetChecksumAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var properties = await container.GetBlobClient(path).GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return properties.Value.Metadata.TryGetValue(ChecksumKey, out var checksum) ? checksum : null;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>Deletes every blob for a run, for the retention sweep where the container has no lifecycle policy.</summary>
    public async Task<int> DeleteRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        var artifacts = await ListAsync(runId, cancellationToken).ConfigureAwait(false);

        foreach (var artifact in artifacts)
        {
            await container.GetBlobClient(artifact.Path)
                .DeleteIfExistsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return artifacts.Count;
    }
}
