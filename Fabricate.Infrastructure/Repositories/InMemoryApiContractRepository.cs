using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Repositories;

public sealed class InMemoryApiContractRepository : IApiContractRepository
{
    private readonly Dictionary<Guid, ApiContract> _contracts = [];
    private readonly Dictionary<Guid, GeneratedApiEndpoint> _endpoints = [];
    private readonly object _lock = new();

    public Task<ApiContract> SaveAsync(ApiContract contract, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        lock (_lock) _contracts[contract.Id] = contract;
        return Task.FromResult(contract);
    }

    public Task<ApiContract?> GetByIdAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        lock (_lock) return Task.FromResult(_contracts.GetValueOrDefault(contractId));
    }

    public Task<IReadOnlyList<ApiContract>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<ApiContract> results = _contracts.Values
                .Where(c => c.WorkspaceId == workspaceId)
                .OrderByDescending(c => c.CreatedAt)
                .ToArray();
            return Task.FromResult(results);
        }
    }

    public Task<GeneratedApiEndpoint> SaveEndpointAsync(GeneratedApiEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (_lock) _endpoints[endpoint.Id] = endpoint;
        return Task.FromResult(endpoint);
    }

    public Task<GeneratedApiEndpoint?> GetEndpointAsync(Guid endpointId, CancellationToken cancellationToken = default)
    {
        lock (_lock) return Task.FromResult(_endpoints.GetValueOrDefault(endpointId));
    }

    public Task<IReadOnlyList<GeneratedApiEndpoint>> ListEndpointsAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<GeneratedApiEndpoint> results = _endpoints.Values
                .Where(e => e.WorkspaceId == workspaceId)
                .OrderBy(e => e.Path, StringComparer.Ordinal)
                .ThenBy(e => e.Method, StringComparer.Ordinal)
                .ToArray();
            return Task.FromResult(results);
        }
    }
}
