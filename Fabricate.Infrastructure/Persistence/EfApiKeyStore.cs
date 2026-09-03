using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Fabricate.Infrastructure.Persistence;

public sealed class EfApiKeyStore(FabricateDbContext db) : IApiKeyStore
{
    public async Task<ApiKey> SaveAsync(ApiKey key, CancellationToken cancellationToken = default)
    {
        var existing = await db.ApiKeys.FindAsync([key.Id], cancellationToken);
        if (existing is null) db.ApiKeys.Add(key);
        else db.Entry(existing).CurrentValues.SetValues(key);
        await db.SaveChangesAsync(cancellationToken);
        return key;
    }

    public Task<ApiKey?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => db.ApiKeys.FindAsync([id], cancellationToken).AsTask();

    public async Task<IReadOnlyList<ApiKey>> ListByAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
        => await db.ApiKeys.Where(k => k.AccountId == accountId).ToListAsync(cancellationToken);

    public Task<ApiKey?> FindByHashAsync(string hashedSecret, CancellationToken cancellationToken = default)
        => db.ApiKeys.FirstOrDefaultAsync(k => k.HashedSecret == hashedSecret, cancellationToken);

    public async Task<ApiKey> UpdateAsync(ApiKey key, CancellationToken cancellationToken = default)
    {
        var existing = await db.ApiKeys.FindAsync([key.Id], cancellationToken)
            ?? throw new InvalidOperationException($"API key '{key.Id}' not found.");
        db.Entry(existing).CurrentValues.SetValues(key);
        await db.SaveChangesAsync(cancellationToken);
        return key;
    }
}
