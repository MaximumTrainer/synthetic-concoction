using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Fabricate.Infrastructure.Persistence;

public sealed class EfRunRepository(FabricateDbContext db) : IRunRepository
{
    public async Task<DatasetRun> CreateAsync(DatasetRun run, CancellationToken cancellationToken = default)
    {
        db.DatasetRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return run;
    }

    public async Task<DatasetRun> UpdateAsync(DatasetRun run, CancellationToken cancellationToken = default)
    {
        var existing = await db.DatasetRuns.FindAsync([run.Id], cancellationToken)
            ?? throw new InvalidOperationException($"Run '{run.Id}' not found.");
        db.Entry(existing).CurrentValues.SetValues(run);
        await db.SaveChangesAsync(cancellationToken);
        return run;
    }

    public Task<DatasetRun?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => db.DatasetRuns.FindAsync([id], cancellationToken).AsTask();

    public async Task<IReadOnlyList<DatasetRun>> ListAsync(int pageSize = 20, int page = 1, CancellationToken cancellationToken = default)
        => await db.DatasetRuns.OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
}
