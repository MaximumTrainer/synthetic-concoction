using System.Security.Cryptography;
using System.Text;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.ApiKeys;

public sealed class ApiKeyService(IApiKeyStore store, IAccountRepository accountRepository) : IApiKeyService
{
    public async Task<(ApiKey Key, string PlaintextSecret)> CreateAsync(CreateApiKeyCommand command, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var membership = await accountRepository.GetMembershipAsync(command.AccountId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (membership?.Role != AccountRole.Owner)
        {
            throw new UnauthorizedAccessException("Only account owners can create API keys.");
        }

        ValidateScopes(command.Scopes);

        var plaintext = GeneratePlaintext();
        var hashed = Hash(plaintext);

        var key = new ApiKey(
            Guid.NewGuid(),
            command.AccountId,
            command.Name,
            hashed,
            command.Scopes,
            DateTimeOffset.UtcNow,
            command.Expiry.HasValue ? DateTimeOffset.UtcNow.Add(command.Expiry.Value) : null);

        await store.SaveAsync(key, cancellationToken).ConfigureAwait(false);
        return (key, plaintext);
    }

    public async Task<ApiKey> RevokeAsync(Guid keyId, Guid requestingUserId, Guid accountId, CancellationToken cancellationToken = default)
    {
        var membership = await accountRepository.GetMembershipAsync(accountId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (membership?.Role != AccountRole.Owner)
        {
            throw new UnauthorizedAccessException("Only account owners can revoke API keys.");
        }

        var key = await store.GetByIdAsync(keyId, cancellationToken).ConfigureAwait(false);
        if (key is null || key.AccountId != accountId)
        {
            throw new InvalidOperationException($"API key '{keyId}' not found.");
        }

        var revoked = key with { RevokedAt = DateTimeOffset.UtcNow };
        return await store.UpdateAsync(revoked, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ApiKey>> ListAsync(Guid accountId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var membership = await accountRepository.GetMembershipAsync(accountId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (membership is null)
        {
            throw new UnauthorizedAccessException("Access denied.");
        }

        return await store.ListByAccountAsync(accountId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiKey?> ValidateAsync(string plaintextSecret, CancellationToken cancellationToken = default)
    {
        var hashed = Hash(plaintextSecret);
        var key = await store.FindByHashAsync(hashed, cancellationToken).ConfigureAwait(false);

        if (key is null || !key.IsActive)
        {
            return null;
        }

        var updated = key with { LastUsedAt = DateTimeOffset.UtcNow };
        await store.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    private static string GeneratePlaintext()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return "cnc_" + Convert.ToBase64String(bytes).Replace("+", "-", StringComparison.Ordinal).Replace("/", "_", StringComparison.Ordinal).TrimEnd('=');
    }

    private static string Hash(string plaintext)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));
        return Convert.ToHexStringLower(bytes);
    }

    private static void ValidateScopes(IReadOnlyList<string> scopes)
    {
        foreach (var scope in scopes)
        {
            if (!ApiKeyScope.All.Contains(scope, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Unknown API key scope: '{scope}'. Valid scopes: {string.Join(", ", ApiKeyScope.All)}.");
            }
        }
    }
}
