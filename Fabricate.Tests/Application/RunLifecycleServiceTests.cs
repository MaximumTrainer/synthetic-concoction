using Fabricate.Application.Generation;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class RunLifecycleServiceTests
{
    private readonly InMemoryRunRepository _repo = new();
    private readonly InMemoryApiKeyStore _artifactStorePlaceholder = new();

    private RunLifecycleService CreateService()
    {
        var artifactStore = new InMemoryArtifactStore();
        return new RunLifecycleService(_repo, artifactStore);
    }

    [Fact]
    public async Task StartRunAsync_ShouldCreateQueuedRun()
    {
        var service = CreateService();
        var rowCounts = new Dictionary<string, int> { ["users"] = 10 };

        var run = await service.StartRunAsync(seed: 42, rowCounts);

        run.Status.Should().Be(RunStatus.Queued);
        run.Seed.Should().Be(42);
        run.RequestedRowCounts.Should().ContainKey("users");
    }

    [Fact]
    public async Task MarkRunningAsync_ShouldTransitionToRunning()
    {
        var service = CreateService();
        var run = await service.StartRunAsync(1, new Dictionary<string, int>());

        var running = await service.MarkRunningAsync(run.Id);

        running.Status.Should().Be(RunStatus.Running);
        running.StartedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CompleteRunAsync_ShouldTransitionToCompleted()
    {
        var service = CreateService();
        var run = await service.StartRunAsync(1, new Dictionary<string, int> { ["t"] = 5 });
        await service.MarkRunningAsync(run.Id);

        var manifest = new RunManifest(run.Id, 1, null, null,
            new Dictionary<string, int> { ["t"] = 5 },
            new Dictionary<string, int> { ["t"] = 5 },
            0,
            new Dictionary<string, string>(),
            [],
            DateTimeOffset.UtcNow);

        var completed = await service.CompleteRunAsync(run.Id, manifest);

        completed.Status.Should().Be(RunStatus.Completed);
        completed.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task FailRunAsync_ShouldTransitionToFailed()
    {
        var service = CreateService();
        var run = await service.StartRunAsync(1, new Dictionary<string, int>());
        await service.MarkRunningAsync(run.Id);

        var failed = await service.FailRunAsync(run.Id, "test error");

        failed.Status.Should().Be(RunStatus.Failed);
        failed.FailureReason.Should().Be("test error");
    }

    [Fact]
    public async Task CancelRunAsync_ShouldCancelQueuedRun()
    {
        var service = CreateService();
        var run = await service.StartRunAsync(1, new Dictionary<string, int>());

        var cancelled = await service.CancelRunAsync(run.Id);

        cancelled.Status.Should().Be(RunStatus.Cancelled);
    }

    [Fact]
    public async Task CancelRunAsync_ShouldThrowForCompletedRun()
    {
        var service = CreateService();
        var run = await service.StartRunAsync(1, new Dictionary<string, int>());
        await service.MarkRunningAsync(run.Id);
        await service.CompleteRunAsync(run.Id, new RunManifest(run.Id, 1, null, null,
            new Dictionary<string, int>(), new Dictionary<string, int>(), 0,
            new Dictionary<string, string>(), [], DateTimeOffset.UtcNow));

        var act = async () => await service.CancelRunAsync(run.Id);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>Simple in-memory IArtifactStore for tests.</summary>
    private sealed class InMemoryArtifactStore : Fabricate.Application.Abstractions.IArtifactStore
    {
        private readonly Dictionary<string, byte[]> _artifacts = [];

        public async Task<string> StoreAsync(string runId, string name, Stream content, CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken);
            var path = $"{runId}/{name}";
            _artifacts[path] = ms.ToArray();
            return path;
        }

        public Task<Stream> RetrieveAsync(string path, CancellationToken cancellationToken = default)
        {
            if (!_artifacts.TryGetValue(path, out var data))
                throw new FileNotFoundException(path);
            return Task.FromResult<Stream>(new MemoryStream(data));
        }

        public Task<IReadOnlyList<Fabricate.Application.Abstractions.StoredArtifact>> ListAsync(string runId, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<Fabricate.Application.Abstractions.StoredArtifact> artifacts = _artifacts
                .Where(kv => kv.Key.StartsWith(runId + "/", StringComparison.Ordinal))
                .Select(kv => new Fabricate.Application.Abstractions.StoredArtifact(
                    kv.Key[(runId.Length + 1)..], kv.Key, kv.Value.LongLength))
                .ToArray();
            return Task.FromResult(artifacts);
        }

        public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult(_artifacts.ContainsKey(path));
    }
}
