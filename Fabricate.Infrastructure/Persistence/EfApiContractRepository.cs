using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Fabricate.Infrastructure.Persistence;

public sealed class EfApiContractRepository(FabricateDbContext db) : IApiContractRepository
{
    public async Task<ApiContract> SaveAsync(ApiContract contract, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var existing = await db.ApiContracts.FindAsync([contract.Id], cancellationToken);
        if (existing is null) db.ApiContracts.Add(contract);
        else db.Entry(existing).CurrentValues.SetValues(contract);

        await db.SaveChangesAsync(cancellationToken);
        return contract;
    }

    public async Task<ApiContract?> GetByIdAsync(Guid contractId, CancellationToken cancellationToken = default)
        => await db.ApiContracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken);

    public async Task<IReadOnlyList<ApiContract>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => await db.ApiContracts.AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

    public async Task<GeneratedApiEndpoint> SaveEndpointAsync(GeneratedApiEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var existing = await db.GeneratedApiEndpoints.FindAsync([endpoint.Id], cancellationToken);
        if (existing is null) db.GeneratedApiEndpoints.Add(endpoint);
        else db.Entry(existing).CurrentValues.SetValues(endpoint);

        await db.SaveChangesAsync(cancellationToken);
        return endpoint;
    }

    public async Task<GeneratedApiEndpoint?> GetEndpointAsync(Guid endpointId, CancellationToken cancellationToken = default)
        => await db.GeneratedApiEndpoints.AsNoTracking().FirstOrDefaultAsync(e => e.Id == endpointId, cancellationToken);

    public async Task<IReadOnlyList<GeneratedApiEndpoint>> ListEndpointsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => await db.GeneratedApiEndpoints.AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId)
            .OrderBy(e => e.Path)
            .ThenBy(e => e.Method)
            .ToListAsync(cancellationToken);
}
