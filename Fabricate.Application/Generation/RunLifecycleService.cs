using Fabricate.Application.Abstractions;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Generation;

public sealed class RunLifecycleService(
    IRunRepository runRepository,
    IArtifactStore artifactStore,
    IWebhookDeliveryService? webhookDeliveryService = null)
{
    public async Task<DatasetRun> StartRunAsync(long seed, IReadOnlyDictionary<string, int> requestedRowCounts, CancellationToken cancellationToken = default)
    {
        var run = new DatasetRun(Guid.NewGuid(), RunStatus.Queued, DateTimeOffset.UtcNow, null, null, seed, null, null, requestedRowCounts);
        return await runRepository.CreateAsync(run, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatasetRun> MarkRunningAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await GetOrThrowAsync(runId, cancellationToken).ConfigureAwait(false);
        return await runRepository.UpdateAsync(run with { Status = RunStatus.Running, StartedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DatasetRun> CompleteRunAsync(Guid runId, RunManifest manifest, CancellationToken cancellationToken = default)
    {
        var run = await GetOrThrowAsync(runId, cancellationToken).ConfigureAwait(false);
        var completed = run with
        {
            Status = RunStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            ValidationIssueCount = manifest.ValidationIssueCount,
            ArtifactChecksums = manifest.ArtifactChecksums,
            ArtifactPaths = manifest.ArtifactPaths
        };
        var saved = await runRepository.UpdateAsync(completed, cancellationToken).ConfigureAwait(false);

        if (webhookDeliveryService is not null && completed.WorkspaceId.HasValue)
        {
            await webhookDeliveryService.DeliverAsync(
                completed.WorkspaceId.Value, "run.completed",
                new { runId = saved.Id, status = saved.Status.ToString(), completedAt = saved.CompletedAt },
                cancellationToken).ConfigureAwait(false);
        }

        return saved;
    }

    public async Task<DatasetRun> FailRunAsync(Guid runId, string reason, CancellationToken cancellationToken = default)
    {
        var run = await GetOrThrowAsync(runId, cancellationToken).ConfigureAwait(false);
        var failed = run with
        {
            Status = RunStatus.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            FailureReason = reason
        };
        var saved = await runRepository.UpdateAsync(failed, cancellationToken).ConfigureAwait(false);

        if (webhookDeliveryService is not null && failed.WorkspaceId.HasValue)
        {
            await webhookDeliveryService.DeliverAsync(
                failed.WorkspaceId.Value, "run.failed",
                new { runId = saved.Id, status = saved.Status.ToString(), reason, failedAt = saved.CompletedAt },
                cancellationToken).ConfigureAwait(false);
        }

        return saved;
    }

    public async Task<DatasetRun> CancelRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await GetOrThrowAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run.Status is not (RunStatus.Queued or RunStatus.Running))
        {
            throw new InvalidOperationException($"Cannot cancel run in status '{run.Status}'.");
        }

        var cancelled = run with { Status = RunStatus.Cancelled, CompletedAt = DateTimeOffset.UtcNow };
        return await runRepository.UpdateAsync(cancelled, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> StoreArtifactAsync(Guid runId, string name, Stream content, CancellationToken cancellationToken = default)
        => await artifactStore.StoreAsync(runId.ToString(), name, content, cancellationToken).ConfigureAwait(false);

    public Task<DatasetRun?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
        => runRepository.GetByIdAsync(runId, cancellationToken);

    public Task<IReadOnlyList<DatasetRun>> ListRunsAsync(int pageSize = 20, int page = 1, CancellationToken cancellationToken = default)
        => runRepository.ListAsync(pageSize, page, cancellationToken);

    private async Task<DatasetRun> GetOrThrowAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await runRepository.GetByIdAsync(runId, cancellationToken).ConfigureAwait(false);
        return run ?? throw new InvalidOperationException($"Run '{runId}' not found.");
    }
}

