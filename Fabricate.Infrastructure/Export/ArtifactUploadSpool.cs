using System.Security.Cryptography;

namespace Fabricate.Infrastructure.Export;

/// <summary>
/// Streams an artifact's bytes through a temp file, hashing and measuring them as they pass (#84, #90).
///
/// <para>
/// Every object-storage adapter needs the same three things at once: a seekable body (the SDKs sign or
/// length-prefix the payload), a SHA-256 to store as metadata, and a byte count. Doing that in memory would
/// undo the streaming export work — the generator can produce files far larger than it is safe to hold — so the
/// bytes go to disk a buffer at a time and the hash is computed on the way past.
/// </para>
///
/// <para>
/// The file is opened <see cref="FileOptions.DeleteOnClose"/>, so a crashed upload leaves nothing behind on the
/// host: the artifact is the customer's data, and a temp file outliving the process is a copy nobody is tracking.
/// </para>
/// </summary>
internal sealed class ArtifactUploadSpool : IAsyncDisposable
{
    private const int BufferSize = 81920;

    internal FileStream Stream { get; } = new(
        Path.Combine(Path.GetTempPath(), $"fabricate-upload-{Guid.NewGuid():N}"),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize,
        FileOptions.DeleteOnClose | FileOptions.Asynchronous);

    internal long Length { get; private set; }

    internal string Checksum { get; private set; } = string.Empty;

    /// <summary>Drains <paramref name="source"/> into the spool and rewinds it, ready to upload.</summary>
    internal static async Task<ArtifactUploadSpool> FillAsync(Stream source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var spool = new ArtifactUploadSpool();
        try
        {
            using var hasher = SHA256.Create();
            var buffer = new byte[BufferSize];

            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hasher.TransformBlock(buffer, 0, read, null, 0);
                await spool.Stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                spool.Length += read;
            }

            hasher.TransformFinalBlock([], 0, 0);
            spool.Checksum = Convert.ToHexStringLower(hasher.Hash!);

            await spool.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            spool.Stream.Position = 0;

            return spool;
        }
        catch
        {
            await spool.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>Shared rules for turning an artifact name into a store key (#84, #90).</summary>
internal static class ArtifactKey
{
    /// <summary>Everything for one run lives under this prefix, which is what makes listing and purging cheap.</summary>
    internal static string Prefix(string runId) => $"runs/{Path.GetFileName(runId)}/";

    /// <summary>
    /// The key for one artifact. Names carry an exporter directory — <c>csv/main_users.csv</c> — so segments are
    /// sanitised individually rather than flattened: flattening would make two formats of one table collide.
    /// Traversal segments are dropped, so a key can never escape its run's prefix.
    /// </summary>
    internal static string For(string runId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var segments = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileName)
            .Where(segment => !string.IsNullOrEmpty(segment) && segment != "." && segment != "..")
            .ToArray();

        if (segments.Length == 0) throw new ArgumentException("Artifact name resolves to nothing.", nameof(name));

        return Prefix(runId) + string.Join('/', segments!);
    }
}
