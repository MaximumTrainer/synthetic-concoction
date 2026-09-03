using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Accounts;

public sealed class AccountService(IAccountRepository accountRepository) : IAccountService
{
    public async Task<Account> CreateAccountAsync(CreateAccountCommand command, CancellationToken cancellationToken = default)
    {
        var account = new Account(Guid.NewGuid(), command.Name, DateTimeOffset.UtcNow);
        await accountRepository.SaveAsync(account, cancellationToken).ConfigureAwait(false);

        var ownership = new AccountMembership(account.Id, command.OwnerId, AccountRole.Owner, DateTimeOffset.UtcNow);
        await accountRepository.AddMemberAsync(ownership, cancellationToken).ConfigureAwait(false);

        return account;
    }

    public Task<Account?> GetByIdAsync(Guid accountId, CancellationToken cancellationToken = default)
        => accountRepository.GetByIdAsync(accountId, cancellationToken);

    public Task<IReadOnlyList<AccountMembership>> GetMembersAsync(Guid accountId, CancellationToken cancellationToken = default)
        => accountRepository.GetMembersAsync(accountId, cancellationToken);

    public async Task EnsureMemberAsync(Guid accountId, Guid userId, CancellationToken cancellationToken = default)
    {
        var membership = await accountRepository.GetMembershipAsync(accountId, userId, cancellationToken).ConfigureAwait(false);
        if (membership is null)
        {
            throw new UnauthorizedAccessException($"User '{userId}' is not a member of account '{accountId}'.");
        }
    }
}
