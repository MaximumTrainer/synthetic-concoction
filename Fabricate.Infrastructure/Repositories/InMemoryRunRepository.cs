using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Repositories;

public sealed class InMemoryRunRepository : IRunRepository
{
    private readonly List<DatasetRun> _runs = [];

    public Task<DatasetRun> CreateAsync(DatasetRun run, CancellationToken cancellationToken = default)
    {
        _runs.Add(run);
        return Task.FromResult(run);
    }

    public Task<DatasetRun> UpdateAsync(DatasetRun run, CancellationToken cancellationToken = default)
    {
        var index = _runs.FindIndex(r => r.Id == run.Id);
        if (index < 0) throw new InvalidOperationException($"Run '{run.Id}' not found.");
        _runs[index] = run;
        return Task.FromResult(run);
    }

    public Task<DatasetRun?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_runs.Find(r => r.Id == id));

    public Task<IReadOnlyList<DatasetRun>> ListAsync(int pageSize = 20, int page = 1, CancellationToken cancellationToken = default)
    {
        var result = _runs.OrderByDescending(r => r.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToArray();
        return Task.FromResult<IReadOnlyList<DatasetRun>>(result);
    }

    public Task<IReadOnlyList<DatasetRun>> ListByWorkspaceAsync(Guid workspaceId, int pageSize = 20, int page = 1, CancellationToken cancellationToken = default)
    {
        var result = _runs
            .Where(r => r.WorkspaceId == workspaceId)
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToArray();

        return Task.FromResult<IReadOnlyList<DatasetRun>>(result);
    }
}
