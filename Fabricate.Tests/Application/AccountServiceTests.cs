using Fabricate.Application.Abstractions;
using Fabricate.Application.Accounts;
using Fabricate.Application.Governance;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class AccountServiceTests
{
    private readonly InMemoryAccountRepository _accountRepo = new();
    private readonly InMemoryAuditLogRepository _auditLogRepo = new();
    private readonly AccountService _accountService;

    public AccountServiceTests()
    {
        _accountService = new AccountService(_accountRepo);
    }

    [Fact]
    public async Task CreateAccountAsync_ShouldPersistAccountAndOwnerMembership()
    {
        var ownerId = Guid.NewGuid();
        var account = await _accountService.CreateAccountAsync(new CreateAccountCommand("Acme Corp", ownerId));

        account.Id.Should().NotBeEmpty();
        account.Name.Should().Be("Acme Corp");

        var members = await _accountService.GetMembersAsync(account.Id);
        members.Should().ContainSingle(m => m.UserId == ownerId && m.Role == AccountRole.Owner);
    }

    [Fact]
    public async Task GetMembersAsync_ShouldReturnEmptyForUnknownAccount()
    {
        var members = await _accountService.GetMembersAsync(Guid.NewGuid());
        members.Should().BeEmpty();
    }

    [Fact]
    public async Task EnsureMemberAsync_ShouldSucceedForExistingMember()
    {
        var ownerId = Guid.NewGuid();
        var account = await _accountService.CreateAccountAsync(new CreateAccountCommand("TestCo", ownerId));

        // Owner is already a member — should not throw
        var act = async () => await _accountService.EnsureMemberAsync(account.Id, ownerId);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task EnsureMemberAsync_ShouldThrowForNonMember()
    {
        var ownerId = Guid.NewGuid();
        var account = await _accountService.CreateAccountAsync(new CreateAccountCommand("TestCo", ownerId));

        var nonMemberId = Guid.NewGuid();
        var act = async () => await _accountService.EnsureMemberAsync(account.Id, nonMemberId);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }
}
