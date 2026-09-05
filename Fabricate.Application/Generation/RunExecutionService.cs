using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Generation;

/// <summary>
/// Starts and reads generation runs on behalf of a workspace member (#66).
///
/// <para>
/// Runs were reachable only from the CLI and the chat tool, and <c>GET /runs</c> listed every run in the instance
/// to any authenticated caller. Everything here is scoped to a workspace the caller belongs to, and a run in
/// another workspace is reported as not found rather than forbidden — a 403 would confirm the id exists.
/// </para>
/// </summary>
public sealed class RunExecutionService(
    RunLifecycleService runs,
    ISyntheticDataOrchestrator orchestrator,
    ISchemaSnapshotService schemaSnapshots,
    IWorkspaceService workspaces,
    IArtifactStore artifactStore,
    IEnumerable<IExporter> exporters) : IRunExecutionService
{
    /// <summary>Matches the CLI's default, so the same inputs give the same outputs on both paths.</summary>
    private static readonly string[] DefaultExporters = ["csv"];

    private static readonly JsonSerializerOptions SummaryJson =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<DatasetRun> StartAsync(StartRunCommand command, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Starting a run consumes the workspace's resources, so it takes Editor rather than Viewer.
        var role = await workspaces.GetEffectiveRoleAsync(command.WorkspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (role is null or < WorkspaceRole.Editor)
        {
            throw new UnauthorizedAccessException("Workspace editors or admins can start runs.");
        }

        var schema = command.SchemaSnapshotId is Guid snapshotId
            ? await schemaSnapshots.RestoreSchemaAsync(command.WorkspaceId, snapshotId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Schema snapshot '{snapshotId}' was not found in this workspace.")
            : await orchestrator.DiscoverAsync(cancellationToken).ConfigureAwait(false);

        // A run whose requested tables are not in the schema would "succeed" having generated nothing, leaving the
        // caller with an empty manifest and no explanation. Refuse it instead.
        var known = schema.Tables.Select(t => t.QualifiedName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = command.RowCounts.Keys.Where(t => !known.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException(
                $"The schema has no table named {string.Join(", ", unknown)}. Available: {string.Join(", ", known.Order(StringComparer.Ordinal))}.",
                nameof(command));
        }

        if (command.RowCounts.Count == 0)
        {
            throw new ArgumentException("A run must request at least one table.", nameof(command));
        }

        var run = await runs.StartRunAsync(
            command.Seed, command.RowCounts, command.WorkspaceId, command.ProjectId, command.SchemaSnapshotId, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await runs.MarkRunningAsync(run.Id, cancellationToken).ConfigureAwait(false);

            var request = new GenerationRequest(schema, command.RowCounts, command.Seed, command.Rules, command.ComplianceProfile);
            var (result, summary) = await orchestrator.GenerateAsync(request, cancellationToken).ConfigureAwait(false);

            var selected = SelectExporters(command.Exporters);
            var checksums = new Dictionary<string, string>(StringComparer.Ordinal);
            var paths = new List<string>();

            // Exported to a temp directory and then handed to the artifact store, so the store — which may not be
            // a file system at all — owns where the bytes end up.
            var staging = Path.Combine(Path.GetTempPath(), $"fabricate-run-{run.Id:N}");
            try
            {
                foreach (var exporter in selected)
                {
                    await exporter.ExportAsync(result.Tables, Path.Combine(staging, exporter.Name), cancellationToken).ConfigureAwait(false);
                }

                await File.WriteAllTextAsync(
                    Path.Combine(EnsureDirectory(staging), "summary.json"),
                    JsonSerializer.Serialize(summary, SummaryJson),
                    cancellationToken).ConfigureAwait(false);

                foreach (var file in Directory.GetFiles(staging, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                {
                    // The artifact's name keeps the exporter directory, so csv/main_users.csv and
                    // json/main_users.json do not collide once flattened into one store.
                    var name = Path.GetRelativePath(staging, file).Replace('\\', '/');

                    await using (var content = File.OpenRead(file))
                    {
                        paths.Add(await artifactStore.StoreAsync(run.Id.ToString(), name, content, cancellationToken).ConfigureAwait(false));
                    }

                    checksums[name] = await ChecksumAsync(file, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }

            var actualRowCounts = result.Tables.ToDictionary(t => t.Table, t => t.Rows.Count, StringComparer.Ordinal);

            var manifest = new RunManifest(
                run.Id,
                command.Seed,
                command.SchemaSnapshotId,
                ProfileSnapshotId: null,
                command.RowCounts,
                actualRowCounts,
                result.ValidationIssues.Count,
                checksums,
                paths,
                summary.CompletedAt);

            return await runs.CompleteRunAsync(run.Id, manifest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await runs.CancelRunAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // The run row records the failure; without this a failed run would sit at Running forever.
            await runs.FailRunAsync(run.Id, ex.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<DatasetRun>> ListAsync(Guid workspaceId, Guid requestingUserId, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (!await HasAccessAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false)) return [];
        return await runs.ListRunsAsync(workspaceId, pageSize, page, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatasetRun?> GetAsync(Guid workspaceId, Guid runId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        if (!await HasAccessAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false)) return null;

        var run = await runs.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        return run?.WorkspaceId == workspaceId ? run : null;
    }

    public async Task<DatasetRun?> CancelAsync(Guid workspaceId, Guid runId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var run = await GetAsync(workspaceId, runId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (run is null) return null;

        var role = await workspaces.GetEffectiveRoleAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (role is null or < WorkspaceRole.Editor)
        {
            throw new UnauthorizedAccessException("Workspace editors or admins can cancel runs.");
        }

        return await runs.CancelRunAsync(runId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ArtifactDescriptor>?> ListArtifactsAsync(Guid workspaceId, Guid runId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var run = await GetAsync(workspaceId, runId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (run is null) return null;

        var stored = await artifactStore.ListAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);

        return stored
            .Select(a => new ArtifactDescriptor(
                a.Name,
                a.SizeBytes,
                // The checksum recorded at completion is authoritative; a store that cannot supply one leaves it
                // empty rather than recomputing, which would agree with the file rather than with the run.
                run.ArtifactChecksums?.GetValueOrDefault(a.Name) ?? string.Empty,
                ContentTypeFor(a.Name)))
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<(Stream Content, ArtifactDescriptor Descriptor)?> OpenArtifactAsync(Guid workspaceId, Guid runId, string name, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var descriptors = await ListArtifactsAsync(workspaceId, runId, requestingUserId, cancellationToken).ConfigureAwait(false);
        var descriptor = descriptors?.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
        if (descriptor is null) return null;

        var stored = await artifactStore.ListAsync(runId.ToString(), cancellationToken).ConfigureAwait(false);
        var match = stored.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.Ordinal));
        if (match is null) return null;

        var content = await artifactStore.RetrieveAsync(match.Path, cancellationToken).ConfigureAwait(false);
        return (content, descriptor);
    }

    private async Task<bool> HasAccessAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
        => await workspaces.GetEffectiveRoleAsync(workspaceId, userId, cancellationToken).ConfigureAwait(false) is not null;

    private IReadOnlyList<IExporter> SelectExporters(IReadOnlyList<string>? names)
    {
        var wanted = names is { Count: > 0 } ? names : DefaultExporters;
        var selected = exporters
            .Where(e => wanted.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (selected.Length == 0)
        {
            throw new ArgumentException(
                $"No exporter matches '{string.Join(", ", wanted)}'. Available: {string.Join(", ", exporters.Select(e => e.Name))}.",
                nameof(names));
        }

        return selected;
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string> ChecksumAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".sql" => "application/sql",
        ".parquet" => "application/vnd.apache.parquet",
        _ => "application/octet-stream",
    };
}
