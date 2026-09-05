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

        // Names may carry an exporter directory (csv/main_users.csv), so the segments are sanitised individually
        // rather than flattened — but every segment is still stripped of any traversal.
        var segments = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileName)
            .Where(seg => !string.IsNullOrEmpty(seg) && seg != "." && seg != "..")
            .ToArray();

        if (segments.Length == 0) throw new ArgumentException("Artifact name resolves to nothing.", nameof(name));

        var path = Path.Combine([dir, .. segments!]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

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

    public Task<IReadOnlyList<StoredArtifact>> ListAsync(string runId, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(baseDirectory, Path.GetFileName(runId));
        if (!Directory.Exists(dir)) return Task.FromResult<IReadOnlyList<StoredArtifact>>([]);

        IReadOnlyList<StoredArtifact> artifacts = Directory
            .GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => new StoredArtifact(
                Path.GetRelativePath(dir, f).Replace('\\', '/'),
                f,
                new FileInfo(f).Length))
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ToArray();

        return Task.FromResult(artifacts);
    }

    /// <summary>Removes every artifact stored for a run. Returns how many files went (#84).</summary>
    public int DeleteRun(string runId)
    {
        var dir = Path.Combine(baseDirectory, Path.GetFileName(runId));
        if (!Directory.Exists(dir)) return 0;

        var count = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length;
        Directory.Delete(dir, recursive: true);
        return count;
    }

    /// <summary>Computes a SHA-256 checksum hex string for the file at <paramref name="path"/>.</summary>
    public static async Task<string> ComputeChecksumAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
