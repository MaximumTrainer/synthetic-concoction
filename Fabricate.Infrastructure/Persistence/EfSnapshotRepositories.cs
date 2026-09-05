using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Fabricate.Infrastructure.Persistence;

public sealed class EfSchemaSnapshotRepository(FabricateDbContext db) : ISchemaSnapshotRepository
{
    public async Task SaveAsync(SchemaSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Snapshots are immutable once taken, so an id that already exists is a re-save of the same content.
        var existing = await db.SchemaSnapshots.FindAsync([snapshot.Id], cancellationToken);
        if (existing is null) db.SchemaSnapshots.Add(snapshot);
        else db.Entry(existing).CurrentValues.SetValues(snapshot);

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<SchemaSnapshot?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => await db.SchemaSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<SchemaSnapshot?> GetLatestAsync(string databaseName, CancellationToken cancellationToken = default)
        => await db.SchemaSnapshots.AsNoTracking()
            .Where(s => s.DatabaseName == databaseName)
            .OrderByDescending(s => s.Version)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SchemaSnapshot>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => await db.SchemaSnapshots.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId)
            .OrderByDescending(s => s.Version)
            .ToListAsync(cancellationToken);

    public async Task<int> MaxVersionAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => await db.SchemaSnapshots.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId)
            .Select(s => (int?)s.Version)
            .MaxAsync(cancellationToken) ?? 0;
}

public sealed class EfProfileSnapshotRepository(FabricateDbContext db) : IProfileSnapshotRepository
{
    public async Task SaveAsync(ProfileSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var existing = await db.ProfileSnapshots.FindAsync([snapshot.Id], cancellationToken);
        if (existing is null) db.ProfileSnapshots.Add(snapshot);
        else db.Entry(existing).CurrentValues.SetValues(snapshot);

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ProfileSnapshot?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => await db.ProfileSnapshots.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task<ProfileSnapshot?> GetLatestAsync(string databaseName, CancellationToken cancellationToken = default)
        => await db.ProfileSnapshots.AsNoTracking()
            .Where(p => p.DatabaseName == databaseName)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ProfileSnapshot>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => await db.ProfileSnapshots.AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId)
            .OrderByDescending(p => p.Version)
            .ToListAsync(cancellationToken);

    public async Task<int> MaxVersionAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => await db.ProfileSnapshots.AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId)
            .Select(p => (int?)p.Version)
            .MaxAsync(cancellationToken) ?? 0;
}
