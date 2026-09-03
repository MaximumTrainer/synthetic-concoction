using Fabricate.Application.Abstractions;
using Fabricate.Application.ApiKeys;
using Fabricate.Application.Governance;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class ApiKeyServiceTests
{
    private readonly InMemoryAccountRepository _accountRepo = new();
    private readonly InMemoryAuditLogRepository _auditLogRepo = new();
    private readonly InMemoryApiKeyStore _keyStore = new();
    private readonly ApiKeyService _service;

    public ApiKeyServiceTests()
    {
        var auditLogService = new AuditLogService(_auditLogRepo);
        _service = new ApiKeyService(_keyStore, _accountRepo);
    }

    private async Task<(Guid accountId, Guid ownerId)> CreateAccountWithOwnerAsync()
    {
        var ownerId = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), "Test Org", DateTimeOffset.UtcNow);
        await _accountRepo.SaveAsync(account);
        await _accountRepo.AddMemberAsync(new AccountMembership(account.Id, ownerId, AccountRole.Owner, DateTimeOffset.UtcNow));
        return (account.Id, ownerId);
    }

    [Fact]
    public async Task CreateAsync_ShouldReturnKeyWithPlaintextAndHashedSecret()
    {
        var (accountId, ownerId) = await CreateAccountWithOwnerAsync();

        var (key, plaintext) = await _service.CreateAsync(new CreateApiKeyCommand(accountId, "test-key", [ApiKeyScope.Read]), ownerId);

        plaintext.Should().StartWith("cnc_");
        key.HashedSecret.Should().NotBe(plaintext);
        key.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_ShouldReturnKeyForValidSecret()
    {
        var (accountId, ownerId) = await CreateAccountWithOwnerAsync();
        var (_, plaintext) = await _service.CreateAsync(new CreateApiKeyCommand(accountId, "test-key", [ApiKeyScope.Read]), ownerId);

        var validated = await _service.ValidateAsync(plaintext);

        validated.Should().NotBeNull();
        validated!.AccountId.Should().Be(accountId);
    }

    [Fact]
    public async Task ValidateAsync_ShouldReturnNullForInvalidSecret()
    {
        var result = await _service.ValidateAsync("cnc_notavalidkey123");
        result.Should().BeNull();
    }

    [Fact]
    public async Task RevokeAsync_ShouldMakeKeyInactive()
    {
        var (accountId, ownerId) = await CreateAccountWithOwnerAsync();
        var (key, plaintext) = await _service.CreateAsync(new CreateApiKeyCommand(accountId, "test-key", [ApiKeyScope.Write]), ownerId);

        await _service.RevokeAsync(key.Id, ownerId, accountId);

        var result = await _service.ValidateAsync(plaintext);
        result.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowForNonOwner()
    {
        var (accountId, _) = await CreateAccountWithOwnerAsync();
        var nonOwnerId = Guid.NewGuid();
        await _accountRepo.AddMemberAsync(new AccountMembership(accountId, nonOwnerId, AccountRole.Member, DateTimeOffset.UtcNow));

        var act = async () => await _service.CreateAsync(new CreateApiKeyCommand(accountId, "key", [ApiKeyScope.Read]), nonOwnerId);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task CreateAsync_ShouldThrowForInvalidScope()
    {
        var (accountId, ownerId) = await CreateAccountWithOwnerAsync();

        var act = async () => await _service.CreateAsync(new CreateApiKeyCommand(accountId, "key", ["invalid-scope"]), ownerId);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Unknown API key scope*");
    }
}
