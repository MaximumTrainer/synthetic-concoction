using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Schema;

/// <summary>
/// Versioned, workspace-scoped schema snapshots (#75).
///
/// <para>
/// Reads take the workspace as well as the id and return null for a snapshot belonging to another one. A stored
/// schema is a description of a customer's database — table and column names, relationships — so a snapshot id
/// must not be an existence oracle across tenants.
/// </para>
/// </summary>
public sealed class SchemaSnapshotService(ISchemaSnapshotRepository repository) : ISchemaSnapshotService
{
    public async Task<SchemaSnapshot> SaveSnapshotAsync(
        Guid workspaceId,
        DatabaseSchema schema,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var version = await repository.MaxVersionAsync(workspaceId, cancellationToken).ConfigureAwait(false) + 1;
        var snapshot = new SchemaSnapshot(
            Id: Guid.NewGuid(),
            DatabaseName: schema.Name,
            Version: version,
            CapturedAt: DateTimeOffset.UtcNow,
            Schema: schema,
            WorkspaceId: workspaceId);

        await repository.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<SchemaSnapshot?> GetSnapshotAsync(Guid workspaceId, Guid snapshotId, CancellationToken cancellationToken = default)
    {
        var snapshot = await repository.GetByIdAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        return snapshot?.WorkspaceId == workspaceId ? snapshot : null;
    }

    public Task<IReadOnlyList<SchemaSnapshot>> ListSnapshotsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => repository.ListByWorkspaceAsync(workspaceId, cancellationToken);

    public async Task<DatabaseSchema?> RestoreSchemaAsync(Guid workspaceId, Guid snapshotId, CancellationToken cancellationToken = default)
        => (await GetSnapshotAsync(workspaceId, snapshotId, cancellationToken).ConfigureAwait(false))?.Schema;
}

/// <summary>Versioned, workspace-scoped profile (aggregate statistics) snapshots (#75).</summary>
public sealed class ProfileSnapshotService(IProfileSnapshotRepository repository) : IProfileSnapshotService
{
    public async Task<ProfileSnapshot> SaveProfileAsync(
        Guid workspaceId,
        ProfileSnapshot profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var version = await repository.MaxVersionAsync(workspaceId, cancellationToken).ConfigureAwait(false) + 1;
        var versioned = profile with
        {
            Id = Guid.NewGuid(),
            Version = version,
            WorkspaceId = workspaceId,
            CapturedAt = profile.CapturedAt == default ? DateTimeOffset.UtcNow : profile.CapturedAt,
        };

        await repository.SaveAsync(versioned, cancellationToken).ConfigureAwait(false);
        return versioned;
    }

    public async Task<ProfileSnapshot?> GetProfileAsync(Guid workspaceId, Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = await repository.GetByIdAsync(profileId, cancellationToken).ConfigureAwait(false);
        return profile?.WorkspaceId == workspaceId ? profile : null;
    }

    public Task<IReadOnlyList<ProfileSnapshot>> ListProfilesAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => repository.ListByWorkspaceAsync(workspaceId, cancellationToken);
}
