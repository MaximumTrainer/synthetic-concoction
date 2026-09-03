using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Microsoft.EntityFrameworkCore;

namespace Fabricate.Infrastructure.Persistence;

public sealed class EfAccountRepository(FabricateDbContext db) : IAccountRepository
{
    public async Task<Account> SaveAsync(Account account, CancellationToken cancellationToken = default)
    {
        var existing = await db.Accounts.FindAsync([account.Id], cancellationToken);
        if (existing is null) db.Accounts.Add(account);
        else db.Entry(existing).CurrentValues.SetValues(account);
        await db.SaveChangesAsync(cancellationToken);
        return account;
    }

    public Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => db.Accounts.FindAsync([id], cancellationToken).AsTask();

    public async Task<AccountMembership> AddMemberAsync(AccountMembership membership, CancellationToken cancellationToken = default)
    {
        var existing = await db.AccountMemberships
            .FindAsync([membership.AccountId, membership.UserId], cancellationToken);
        if (existing is null) db.AccountMemberships.Add(membership);
        else db.Entry(existing).CurrentValues.SetValues(membership);
        await db.SaveChangesAsync(cancellationToken);
        return membership;
    }

    public Task<IReadOnlyList<AccountMembership>> GetMembersAsync(Guid accountId, CancellationToken cancellationToken = default)
        => db.AccountMemberships.Where(m => m.AccountId == accountId)
            .ToListAsync(cancellationToken)
            .ContinueWith(t => (IReadOnlyList<AccountMembership>)t.Result, TaskContinuationOptions.ExecuteSynchronously);

    public Task<AccountMembership?> GetMembershipAsync(Guid accountId, Guid userId, CancellationToken cancellationToken = default)
        => db.AccountMemberships.FindAsync([accountId, userId], cancellationToken).AsTask();
}
