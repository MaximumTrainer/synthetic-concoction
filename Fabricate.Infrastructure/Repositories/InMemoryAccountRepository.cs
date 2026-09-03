using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Repositories;

public sealed class InMemoryAccountRepository : IAccountRepository
{
    private readonly List<Account> _accounts = [];
    private readonly List<AccountMembership> _memberships = [];

    public Task<Account> SaveAsync(Account account, CancellationToken cancellationToken = default)
    {
        _accounts.RemoveAll(a => a.Id == account.Id);
        _accounts.Add(account);
        return Task.FromResult(account);
    }

    public Task<Account?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_accounts.Find(a => a.Id == id));

    public Task<AccountMembership> AddMemberAsync(AccountMembership membership, CancellationToken cancellationToken = default)
    {
        _memberships.RemoveAll(m => m.AccountId == membership.AccountId && m.UserId == membership.UserId);
        _memberships.Add(membership);
        return Task.FromResult(membership);
    }

    public Task<IReadOnlyList<AccountMembership>> GetMembersAsync(Guid accountId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AccountMembership>>(_memberships.Where(m => m.AccountId == accountId).ToArray());

    public Task<AccountMembership?> GetMembershipAsync(Guid accountId, Guid userId, CancellationToken cancellationToken = default)
        => Task.FromResult(_memberships.Find(m => m.AccountId == accountId && m.UserId == userId));
}
