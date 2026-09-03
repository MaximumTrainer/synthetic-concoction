using Fabricate.Application.Abstractions;
using Fabricate.Application.Accounts;
using Fabricate.Application.Governance;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class InvitationServiceTests
{
    private readonly InMemoryAccountRepository _accountRepo = new();
    private readonly InMemoryUserRepository _userRepo = new();
    private readonly InMemoryAuditLogRepository _auditLogRepo = new();
    private readonly IAuditLogService _auditLogService;
    private readonly IAllowedDomainService _allowedDomainService;
    private readonly InvitationService _service;

    public InvitationServiceTests()
    {
        _auditLogService = new AuditLogService(_auditLogRepo);
        _allowedDomainService = new AllowedDomainService(_accountRepo, _auditLogService);
        _service = new InvitationService(_userRepo, _accountRepo, _allowedDomainService, _auditLogService);
    }

    private async Task<(Guid accountId, Guid ownerId)> CreateAccountAsync(string domain = "example.com")
    {
        var ownerId = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), "Test Org", DateTimeOffset.UtcNow);
        await _accountRepo.SaveAsync(account);
        var membership = new AccountMembership(account.Id, ownerId, AccountRole.Owner, DateTimeOffset.UtcNow);
        await _accountRepo.AddMemberAsync(membership);
        await _allowedDomainService.AddDomainAsync(account.Id, domain, ownerId);
        return (account.Id, ownerId);
    }

    [Fact]
    public async Task InviteAsync_ShouldCreateInvitationForAllowedDomain()
    {
        var (accountId, ownerId) = await CreateAccountAsync("example.com");

        var invite = await _service.InviteAsync(new InviteUserCommand(accountId, ownerId, "alice@example.com", TimeSpan.FromDays(7)));

        invite.InviteeEmail.Should().Be("alice@example.com");
        invite.IsActive.Should().BeTrue();
        invite.Token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task InviteAsync_ShouldThrowForBlockedDomain()
    {
        var (accountId, ownerId) = await CreateAccountAsync("allowed.com");

        var act = async () => await _service.InviteAsync(new InviteUserCommand(accountId, ownerId, "user@blocked.com", TimeSpan.FromDays(7)));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not in the allowed-domains list*");
    }

    [Fact]
    public async Task AcceptAsync_ShouldAddMemberToAccount()
    {
        var (accountId, ownerId) = await CreateAccountAsync("example.com");
        var invite = await _service.InviteAsync(new InviteUserCommand(accountId, ownerId, "bob@example.com", TimeSpan.FromDays(7)));

        var userId = Guid.NewGuid();
        var membership = await _service.AcceptAsync(new AcceptInvitationCommand(invite.Token, userId));

        membership.AccountId.Should().Be(accountId);
        membership.UserId.Should().Be(userId);
    }

    [Fact]
    public async Task AcceptAsync_ShouldThrowForExpiredInvitation()
    {
        var (accountId, ownerId) = await CreateAccountAsync("example.com");
        var invite = await _service.InviteAsync(new InviteUserCommand(accountId, ownerId, "charlie@example.com", TimeSpan.FromMilliseconds(1)));

        await Task.Delay(10); // ensure expiry

        var act = async () => await _service.AcceptAsync(new AcceptInvitationCommand(invite.Token, Guid.NewGuid()));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*expired*");
    }

    [Fact]
    public async Task RevokeAsync_ShouldInvalidateInvitation()
    {
        var (accountId, ownerId) = await CreateAccountAsync("example.com");
        var invite = await _service.InviteAsync(new InviteUserCommand(accountId, ownerId, "dave@example.com", TimeSpan.FromDays(7)));

        await _service.RevokeAsync(new RevokeInvitationCommand(invite.Id, ownerId));

        var saved = await _userRepo.GetInvitationByIdAsync(invite.Id);
        saved!.IsRevoked.Should().BeTrue();
    }
}
