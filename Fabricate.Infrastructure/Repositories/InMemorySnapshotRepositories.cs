using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Repositories;

public sealed class InMemorySchemaSnapshotRepository : ISchemaSnapshotRepository
{
    private readonly Dictionary<Guid, SchemaSnapshot> _snapshots = [];
    private readonly object _lock = new();

    public Task SaveAsync(SchemaSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_lock) _snapshots[snapshot.Id] = snapshot;
        return Task.CompletedTask;
    }

    public Task<SchemaSnapshot?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_lock) return Task.FromResult(_snapshots.GetValueOrDefault(id));
    }

    public Task<SchemaSnapshot?> GetLatestAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var latest = _snapshots.Values
                .Where(s => string.Equals(s.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Version)
                .FirstOrDefault();
            return Task.FromResult(latest);
        }
    }

    public Task<IReadOnlyList<SchemaSnapshot>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<SchemaSnapshot> results = _snapshots.Values
                .Where(s => s.WorkspaceId == workspaceId)
                .OrderByDescending(s => s.Version)
                .ToArray();
            return Task.FromResult(results);
        }
    }

    public Task<int> MaxVersionAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var max = _snapshots.Values.Where(s => s.WorkspaceId == workspaceId).Select(s => s.Version).DefaultIfEmpty(0).Max();
            return Task.FromResult(max);
        }
    }
}

public sealed class InMemoryProfileSnapshotRepository : IProfileSnapshotRepository
{
    private readonly Dictionary<Guid, ProfileSnapshot> _profiles = [];
    private readonly object _lock = new();

    public Task SaveAsync(ProfileSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_lock) _profiles[snapshot.Id] = snapshot;
        return Task.CompletedTask;
    }

    public Task<ProfileSnapshot?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_lock) return Task.FromResult(_profiles.GetValueOrDefault(id));
    }

    public Task<ProfileSnapshot?> GetLatestAsync(string databaseName, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var latest = _profiles.Values
                .Where(p => string.Equals(p.DatabaseName, databaseName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.Version)
                .FirstOrDefault();
            return Task.FromResult(latest);
        }
    }

    public Task<IReadOnlyList<ProfileSnapshot>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<ProfileSnapshot> results = _profiles.Values
                .Where(p => p.WorkspaceId == workspaceId)
                .OrderByDescending(p => p.Version)
                .ToArray();
            return Task.FromResult(results);
        }
    }

    public Task<int> MaxVersionAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var max = _profiles.Values.Where(p => p.WorkspaceId == workspaceId).Select(p => p.Version).DefaultIfEmpty(0).Max();
            return Task.FromResult(max);
        }
    }
}
