using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Repositories;

public sealed class InMemoryLlmCredentialStore : ILlmCredentialStore
{
    private readonly Dictionary<Guid, LlmCredential> _credentials = [];
    private readonly Dictionary<Guid, WorkspaceLlmPolicy> _policies = [];
    private readonly object _lock = new();

    public Task<LlmCredential> SaveAsync(LlmCredential credential, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _credentials[credential.Id] = credential;
        }
        return Task.FromResult(credential);
    }

    public Task<LlmCredential?> GetByIdAsync(Guid credentialId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_credentials.GetValueOrDefault(credentialId));
        }
    }

    public Task<IReadOnlyList<LlmCredential>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            IReadOnlyList<LlmCredential> result = _credentials.Values.Where(c => c.WorkspaceId == workspaceId).OrderBy(c => c.CreatedAt).ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<WorkspaceLlmPolicy?> GetPolicyAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_policies.GetValueOrDefault(workspaceId));
        }
    }

    public Task<WorkspaceLlmPolicy> SavePolicyAsync(WorkspaceLlmPolicy policy, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _policies[policy.WorkspaceId] = policy;
        }
        return Task.FromResult(policy);
    }
}
