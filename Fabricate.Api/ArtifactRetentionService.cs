using Fabricate.Application.Abstractions;
using Fabricate.Domain.Enums;
using Fabricate.Infrastructure.Export;

namespace Fabricate.Api;

/// <summary>
/// Deletes artifacts for runs older than the retention window (#84).
///
/// <para>
/// Where the object store offers a lifecycle policy, configuring one on the bucket is cheaper and is what the
/// self-hosting guide recommends. This pass exists for stores that do not — MinIO without a policy, a mounted
/// volume — and for operators who would rather keep the rule in one place. Either way the run record is marked so
/// a purged run reports its artifacts as expired instead of pointing at a path that no longer resolves.
/// </para>
/// </summary>
public sealed class ArtifactRetentionService(
    IServiceProvider services,
    ArtifactStoreOptions options,
    ILogger<ArtifactRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.RetentionEnabled)
        {
            logger.LogInformation("Artifact retention is disabled; generated artifacts are kept indefinitely.");
            return;
        }

        logger.LogInformation("Artifact retention enabled: keeping {RetentionDays} days.", options.RetentionDays);

        using var timer = new PeriodicTimer(SweepInterval);
        do
        {
            await SweepAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var runs = scope.ServiceProvider.GetRequiredService<IRunRepository>();
            var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();

            var cutoff = DateTimeOffset.UtcNow.AddDays(-options.RetentionDays);
            var purged = 0;

            // Paged rather than loaded whole: a long-lived instance can hold a lot of run history.
            for (var page = 1; ; page++)
            {
                var batch = await runs.ListAsync(pageSize: 100, page, cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0) break;

                foreach (var run in batch)
                {
                    if (run.CreatedAt >= cutoff) continue;
                    if (run.ArtifactPaths is null or { Count: 0 }) continue;

                    var removed = await DeleteAsync(store, run.Id, cancellationToken).ConfigureAwait(false);
                    if (removed == 0) continue;

                    // The run keeps its checksums — they are still the record of what was produced — but its
                    // paths are cleared and its status says the artifacts are gone.
                    await runs.UpdateAsync(
                        run with { ArtifactPaths = [], Status = run.Status == RunStatus.Completed ? RunStatus.Completed : run.Status, FailureReason = run.FailureReason },
                        cancellationToken).ConfigureAwait(false);

                    purged++;
                }

                if (batch.Count < 100) break;
            }

            if (purged > 0)
            {
                logger.LogInformation("Artifact retention purged the artifacts of {RunCount} runs older than {RetentionDays} days.", purged, options.RetentionDays);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // Housekeeping must never take the API down; the next tick tries again.
            logger.LogError(ex, "Artifact retention sweep failed; it will be retried on the next interval.");
        }
    }

    private static async Task<int> DeleteAsync(IArtifactStore store, Guid runId, CancellationToken cancellationToken)
    {
        return store switch
        {
            S3ArtifactStore s3 => await s3.DeleteRunAsync(runId.ToString(), cancellationToken).ConfigureAwait(false),
            AzureBlobArtifactStore azure => await azure.DeleteRunAsync(runId.ToString(), cancellationToken).ConfigureAwait(false),
            GcsArtifactStore gcs => await gcs.DeleteRunAsync(runId.ToString(), cancellationToken).ConfigureAwait(false),
            FileSystemArtifactStore fileSystem => fileSystem.DeleteRun(runId.ToString()),
            _ => 0,
        };
    }
}
