using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Schema;

/// <summary>
/// In-memory implementation of schema/profile snapshot versioning.
/// Stores immutable snapshots by ID for reproducible plan creation.
/// </summary>
public sealed class SchemaSnapshotService : ISchemaSnapshotService
{
    private readonly Dictionary<Guid, SchemaSnapshot> _snapshots = new();
    private readonly Dictionary<Guid, int> _versionCounters = new();

    public Task<SchemaSnapshot> SaveSnapshotAsync(
        Guid workspaceId,
        DatabaseSchema schema,
        CancellationToken cancellationToken = default)
    {
        _versionCounters.TryGetValue(workspaceId, out var currentVersion);
        var nextVersion = currentVersion + 1;
        _versionCounters[workspaceId] = nextVersion;

        var snapshot = new SchemaSnapshot(
            Id: Guid.NewGuid(),
            DatabaseName: schema.Name,
            Version: nextVersion,
            CapturedAt: DateTimeOffset.UtcNow,
            Schema: schema,
            WorkspaceId: workspaceId);

        _snapshots[snapshot.Id] = snapshot;
        return Task.FromResult(snapshot);
    }

    public Task<SchemaSnapshot?> GetSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default)
    {
        _snapshots.TryGetValue(snapshotId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<SchemaSnapshot>> ListSnapshotsAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SchemaSnapshot> results = _snapshots.Values
            .Where(s => s.WorkspaceId == workspaceId)
            .OrderByDescending(static s => s.Version)
            .ToArray();
        return Task.FromResult(results);
    }

    public Task<DatabaseSchema?> RestoreSchemaAsync(Guid snapshotId, CancellationToken cancellationToken = default)
    {
        _snapshots.TryGetValue(snapshotId, out var snapshot);
        return Task.FromResult(snapshot?.Schema);
    }
}

/// <summary>
/// In-memory implementation of profile (aggregate statistics) snapshot versioning.
/// </summary>
public sealed class ProfileSnapshotService : IProfileSnapshotService
{
    private readonly Dictionary<Guid, ProfileSnapshot> _profiles = new();
    private readonly Dictionary<Guid, int> _versionCounters = new();

    public Task<ProfileSnapshot> SaveProfileAsync(
        Guid workspaceId,
        ProfileSnapshot profile,
        CancellationToken cancellationToken = default)
    {
        _versionCounters.TryGetValue(workspaceId, out var currentVersion);
        var versioned = profile with { Id = Guid.NewGuid() };
        _versionCounters[workspaceId] = currentVersion + 1;
        _profiles[versioned.Id] = versioned;
        return Task.FromResult(versioned);
    }

    public Task<ProfileSnapshot?> GetProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        _profiles.TryGetValue(profileId, out var profile);
        return Task.FromResult(profile);
    }

    public Task<IReadOnlyList<ProfileSnapshot>> ListProfilesAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        // ProfileSnapshot doesn't carry WorkspaceId yet — return all ordered by capture time.
        IReadOnlyList<ProfileSnapshot> results = _profiles.Values
            .OrderByDescending(static p => p.CapturedAt)
            .ToArray();
        return Task.FromResult(results);
    }
}

