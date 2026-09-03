using System.Security.Cryptography;
using Fabricate.Application.Abstractions;

namespace Fabricate.Infrastructure.Export;

/// <summary>Stores artifacts on the local file system under a run-scoped directory.</summary>
public sealed class FileSystemArtifactStore(string baseDirectory) : IArtifactStore
{
    public async Task<string> StoreAsync(string runId, string name, Stream content, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(baseDirectory, runId);
        Directory.CreateDirectory(dir);

        var safeName = Path.GetFileName(name);
        var path = Path.Combine(dir, safeName);

        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);

        return path;
    }

    public Task<Stream> RetrieveAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Artifact not found at path '{path}'.", path);
        }

        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true));
    }

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(File.Exists(path));

    /// <summary>Computes a SHA-256 checksum hex string for the file at <paramref name="path"/>.</summary>
    public static async Task<string> ComputeChecksumAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
