using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Fabricate.Infrastructure.Persistence;

public sealed class EfLlmUsageRepository(FabricateDbContext db) : ILlmUsageRepository
{
    public async Task RecordAsync(LlmUsageRecord record, CancellationToken cancellationToken = default)
    {
        db.LlmUsageRecords.Add(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<LlmUsageSummary> SummariseWorkspaceAsync(Guid workspaceId, DateTimeOffset from, DateTimeOffset to, LlmUsageGrouping groupBy, CancellationToken cancellationToken = default)
        => SummariseWorkspacesAsync([workspaceId], from, to, groupBy, cancellationToken);

    public async Task<LlmUsageSummary> SummariseWorkspacesAsync(IReadOnlyCollection<Guid> workspaceIds, DateTimeOffset from, DateTimeOffset to, LlmUsageGrouping groupBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspaceIds);
        if (workspaceIds.Count == 0)
        {
            return new LlmUsageSummary(from, to, groupBy, 0, 0, 0, 0, []);
        }

        // Grouping happens in memory rather than in SQL. DateTimeOffset is stored as binary ticks so the provider
        // cannot group by calendar day, and doing one grouping in SQL and another here would risk the two
        // disagreeing about what a bucket is. The window is bounded by from/to, which is what keeps this sane.
        var records = await Window(workspaceIds, from, to).ToListAsync(cancellationToken);
        return LlmUsageRollup.Build(records, from, to, groupBy);
    }

    public async Task<long> TotalTokensAsync(Guid workspaceId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        // Summed in the database: this runs before every chat turn, so it must not read the window into memory.
        var totals = await Window([workspaceId], from, to)
            .GroupBy(_ => 1)
            .Select(g => new { Input = g.Sum(r => r.InputTokens), Output = g.Sum(r => r.OutputTokens) })
            .FirstOrDefaultAsync(cancellationToken);

        return totals is null ? 0 : totals.Input + totals.Output;
    }

    private IQueryable<LlmUsageRecord> Window(IReadOnlyCollection<Guid> workspaceIds, DateTimeOffset from, DateTimeOffset to)
        => db.LlmUsageRecords
            .AsNoTracking()
            .Where(r => workspaceIds.Contains(r.WorkspaceId) && r.OccurredAt >= from && r.OccurredAt < to);
}
