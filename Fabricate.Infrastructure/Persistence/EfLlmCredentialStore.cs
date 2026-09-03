using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Fabricate.Infrastructure.Persistence;

public sealed class EfLlmCredentialStore(FabricateDbContext db) : ILlmCredentialStore
{
    public async Task<LlmCredential> SaveAsync(LlmCredential credential, CancellationToken cancellationToken = default)
    {
        var existing = await db.LlmCredentials.FindAsync([credential.Id], cancellationToken);
        if (existing is null) db.LlmCredentials.Add(credential);
        else db.Entry(existing).CurrentValues.SetValues(credential);
        await db.SaveChangesAsync(cancellationToken);
        return credential;
    }

    public Task<LlmCredential?> GetByIdAsync(Guid credentialId, CancellationToken cancellationToken = default)
        => db.LlmCredentials.FindAsync([credentialId], cancellationToken).AsTask();

    public async Task<IReadOnlyList<LlmCredential>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => await db.LlmCredentials.Where(c => c.WorkspaceId == workspaceId).OrderBy(c => c.CreatedAt).ToListAsync(cancellationToken);

    public Task<WorkspaceLlmPolicy?> GetPolicyAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => db.WorkspaceLlmPolicies.FindAsync([workspaceId], cancellationToken).AsTask();

    public async Task<WorkspaceLlmPolicy> SavePolicyAsync(WorkspaceLlmPolicy policy, CancellationToken cancellationToken = default)
    {
        var existing = await db.WorkspaceLlmPolicies.FindAsync([policy.WorkspaceId], cancellationToken);
        if (existing is null) db.WorkspaceLlmPolicies.Add(policy);
        else db.Entry(existing).CurrentValues.SetValues(policy);
        await db.SaveChangesAsync(cancellationToken);
        return policy;
    }
}
