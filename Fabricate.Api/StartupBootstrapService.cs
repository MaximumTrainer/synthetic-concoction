using System.Security.Cryptography;
using System.Text;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Api;

/// <summary>
/// If <c>FABRICATE__BootstrapApiKey</c> is set, pre-seeds an API key and a fixed bootstrap account
/// so that smoke tests and initial integrations can authenticate without a manual setup step.
/// The bootstrap account ID is always <see cref="BootstrapAccountId"/>.
/// </summary>
public sealed class StartupBootstrapService(
    IApiKeyStore apiKeyStore,
    IAccountRepository accountRepository,
    IConfiguration configuration,
    ILogger<StartupBootstrapService> logger) : IHostedService
{
    /// <summary>Fixed account ID used by the bootstrap API key.</summary>
    public static readonly Guid BootstrapAccountId = new("00000000-0000-0000-0000-000000000001");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var plaintextKey = configuration["FABRICATE:BootstrapApiKey"]
            ?? configuration["FABRICATE__BootstrapApiKey"];

        if (string.IsNullOrWhiteSpace(plaintextKey))
            return;

        logger.LogInformation("Bootstrap API key detected — seeding bootstrap account and key.");

        // Seed the bootstrap account so authentication resolves to a real account.
        var account = new Account(BootstrapAccountId, "Bootstrap", DateTimeOffset.UtcNow);
        await accountRepository.SaveAsync(account, cancellationToken).ConfigureAwait(false);

        var membership = new AccountMembership(
            BootstrapAccountId, BootstrapAccountId, AccountRole.Owner, DateTimeOffset.UtcNow);
        await accountRepository.AddMemberAsync(membership, cancellationToken).ConfigureAwait(false);

        var hashed = HashKey(plaintextKey);
        var existingKey = await apiKeyStore.FindByHashAsync(hashed, cancellationToken).ConfigureAwait(false);
        if (existingKey is not null)
        {
            logger.LogInformation("Bootstrap key already present — skipping seed.");
            return;
        }

        var apiKey = new ApiKey(
            Guid.NewGuid(),
            BootstrapAccountId,
            "bootstrap",
            hashed,
            ApiKeyScope.All,
            DateTimeOffset.UtcNow,
            ExpiresAt: null);

        await apiKeyStore.SaveAsync(apiKey, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Bootstrap key seeded for account {AccountId}.", BootstrapAccountId);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string HashKey(string plaintext)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));
        return Convert.ToHexStringLower(bytes);
    }
}
