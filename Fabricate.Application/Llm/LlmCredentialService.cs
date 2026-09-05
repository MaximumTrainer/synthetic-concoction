using System.Security.Cryptography;
using System.Text;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Llm;

/// <summary>
/// Lifecycle of tenant-supplied LLM credentials. Plaintext exists only in the incoming command,
/// the cipher call, and the outbound probe; every read path returns <see cref="LlmCredentialSummary"/>.
/// </summary>
public sealed class LlmCredentialService(
    ILlmCredentialStore store,
    ISecretCipher cipher,
    IWorkspaceService workspaceService,
    IAuditLogService auditLogService,
    ILlmCredentialProbe probe,
    IPromptDataBoundary promptDataBoundary,
    LlmOptions options) : ILlmCredentialService
{
    public async Task<LlmCredentialSummary> RegisterAsync(RegisterLlmCredentialCommand command, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var workspace = await RequireRoleAsync(command.WorkspaceId, requestingUserId, WorkspaceRole.Admin, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ArgumentException("Credential name is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.Model))
            throw new ArgumentException("Model is required.", nameof(command));
        if (command.Kind == LlmCredentialKind.ApiKey && string.IsNullOrWhiteSpace(command.Secret))
            throw new ArgumentException("Secret is required for API-key credentials.", nameof(command));
        if (!options.IsModelAllowed(command.Model))
            throw new ArgumentException($"Model '{command.Model}' is not in the instance allowlist.", nameof(command));

        var endpoint = NormaliseEndpoint(command.Provider, command.Endpoint);

        var existing = await store.ListByWorkspaceAsync(command.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (existing.Any(c => c.RevokedAt is null && c.Name.Equals(command.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A credential named '{command.Name}' already exists in this workspace.", nameof(command));

        var secret = command.Secret ?? string.Empty;
        var (cipherText, keyVersion) = cipher.Encrypt(secret);

        var credential = new LlmCredential(
            Guid.NewGuid(),
            command.WorkspaceId,
            command.ProjectId,
            command.Name.Trim(),
            command.Provider,
            command.Kind,
            cipherText,
            keyVersion,
            Fingerprint(secret),
            LastFour(secret),
            endpoint,
            command.Model.Trim(),
            command.NonSecretSettings ?? new Dictionary<string, string>(),
            command.IsDefault,
            LlmCredentialStatus.Active,
            DateTimeOffset.UtcNow,
            requestingUserId);

        if (command.IsDefault)
        {
            await ClearOtherDefaultsAsync(existing, credential, cancellationToken).ConfigureAwait(false);
        }

        await store.SaveAsync(credential, cancellationToken).ConfigureAwait(false);
        await AuditAsync(workspace, requestingUserId, "llm_credential.registered", credential, cancellationToken).ConfigureAwait(false);
        return credential.ToSummary();
    }

    public async Task<LlmCredentialSummary> RotateAsync(Guid workspaceId, Guid credentialId, string newSecret, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var workspace = await RequireRoleAsync(workspaceId, requestingUserId, WorkspaceRole.Admin, cancellationToken).ConfigureAwait(false);
        var credential = await GetOwnedOrThrowAsync(workspaceId, credentialId, cancellationToken).ConfigureAwait(false);

        if (credential.Kind == LlmCredentialKind.ApiKey && string.IsNullOrWhiteSpace(newSecret))
            throw new ArgumentException("New secret is required.", nameof(newSecret));
        if (credential.RevokedAt is not null)
            throw new InvalidOperationException("Revoked credentials cannot be rotated.");

        var secret = newSecret ?? string.Empty;
        var (cipherText, keyVersion) = cipher.Encrypt(secret);
        var rotated = credential with
        {
            CipherText = cipherText,
            KeyVersion = keyVersion,
            Fingerprint = Fingerprint(secret),
            LastFour = LastFour(secret),
            Status = LlmCredentialStatus.Active,
            LastValidatedAt = null,
        };

        await store.SaveAsync(rotated, cancellationToken).ConfigureAwait(false);
        await AuditAsync(workspace, requestingUserId, "llm_credential.rotated", rotated, cancellationToken).ConfigureAwait(false);
        return rotated.ToSummary();
    }

    public async Task RevokeAsync(Guid workspaceId, Guid credentialId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var workspace = await RequireRoleAsync(workspaceId, requestingUserId, WorkspaceRole.Admin, cancellationToken).ConfigureAwait(false);
        var credential = await GetOwnedOrThrowAsync(workspaceId, credentialId, cancellationToken).ConfigureAwait(false);

        var revoked = credential with { Status = LlmCredentialStatus.Revoked, RevokedAt = DateTimeOffset.UtcNow, IsDefault = false };
        await store.SaveAsync(revoked, cancellationToken).ConfigureAwait(false);
        await AuditAsync(workspace, requestingUserId, "llm_credential.revoked", revoked, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LlmCredentialSummary>> ListAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        await RequireRoleAsync(workspaceId, requestingUserId, WorkspaceRole.Viewer, cancellationToken).ConfigureAwait(false);
        var credentials = await store.ListByWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return credentials.OrderBy(c => c.CreatedAt).Select(c => c.ToSummary()).ToArray();
    }

    public async Task<LlmCredentialValidationResult> ValidateAsync(Guid workspaceId, Guid credentialId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var workspace = await RequireRoleAsync(workspaceId, requestingUserId, WorkspaceRole.Viewer, cancellationToken).ConfigureAwait(false);
        var credential = await GetOwnedOrThrowAsync(workspaceId, credentialId, cancellationToken).ConfigureAwait(false);

        if (credential.RevokedAt is not null)
        {
            return new LlmCredentialValidationResult(credentialId, false, "Credential has been revoked.", DateTimeOffset.UtcNow);
        }

        var resolved = ToResolved(credential, cipher, LlmCredentialSource.WorkspaceDefault);
        LlmCredentialValidationResult result;
        try
        {
            result = await probe.ProbeAsync(credentialId, resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (LlmProviderException ex)
        {
            result = new LlmCredentialValidationResult(credentialId, false, ex.Message, DateTimeOffset.UtcNow);
        }

        var updated = credential with
        {
            Status = result.IsValid ? LlmCredentialStatus.Active : LlmCredentialStatus.Invalid,
            LastValidatedAt = result.CheckedAt,
        };
        await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        await AuditAsync(workspace, requestingUserId, result.IsValid ? "llm_credential.validated" : "llm_credential.validation_failed", updated, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<WorkspaceLlmPolicy> GetPolicyAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        await RequireRoleAsync(workspaceId, requestingUserId, WorkspaceRole.Viewer, cancellationToken).ConfigureAwait(false);
        return await store.GetPolicyAsync(workspaceId, cancellationToken).ConfigureAwait(false)
            ?? new WorkspaceLlmPolicy(workspaceId, false, DateTimeOffset.MinValue);
    }

    public async Task<WorkspaceLlmPolicy> SetPolicyAsync(Guid workspaceId, bool allowPlatformFallback, Guid requestingUserId, IReadOnlyList<string>? allowedTools = null, bool? allowSampledDataInPrompts = null, CancellationToken cancellationToken = default)
    {
        var workspace = await RequireRoleAsync(workspaceId, requestingUserId, WorkspaceRole.Admin, cancellationToken).ConfigureAwait(false);

        var existing = await store.GetPolicyAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var tools = allowedTools is null
            ? existing?.AllowedTools
            : allowedTools.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        // Refused, not ignored: an administrator who is told "saved" while the setting did not take is worse off
        // than one who is told why it cannot (#83). Only an attempt to turn it *on* is refused — turning it off
        // is always allowed, whatever the profile.
        var sampledData = allowSampledDataInPrompts ?? existing?.AllowSampledDataInPrompts ?? false;
        if (sampledData && !promptDataBoundary.CanOptIn(workspace.ComplianceProfile))
        {
            await auditLogService.RecordAsync(new AuditEvent(
                Guid.NewGuid(), workspace.AccountId, requestingUserId,
                "llm.boundary_blocked", "Workspace", workspaceId.ToString(),
                Guid.NewGuid().ToString(), DateTimeOffset.UtcNow,
                $"reason=opt_in_refused;complianceProfile={workspace.ComplianceProfile}"), cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException(promptDataBoundary.OptInRefusalReason(workspace.ComplianceProfile));
        }

        var policy = await store.SavePolicyAsync(
            new WorkspaceLlmPolicy(workspaceId, allowPlatformFallback, DateTimeOffset.UtcNow, tools, sampledData),
            cancellationToken).ConfigureAwait(false);

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), workspace.AccountId, requestingUserId,
            "llm_policy.updated", "Workspace", workspaceId.ToString(),
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow,
            $"allowPlatformFallback={allowPlatformFallback};allowedTools={(tools is null ? "all" : string.Join(",", tools))};allowSampledDataInPrompts={sampledData}"), cancellationToken).ConfigureAwait(false);

        return policy;
    }

    /// <summary>Decrypts a stored credential into its request-scoped form. Shared with the resolver.</summary>
    internal static ResolvedLlmCredential ToResolved(LlmCredential credential, ISecretCipher cipher, LlmCredentialSource source)
    {
        var secret = credential.Kind == LlmCredentialKind.ApiKey
            ? cipher.Decrypt(credential.CipherText, credential.KeyVersion)
            : string.Empty;

        return new ResolvedLlmCredential(
            credential.Provider,
            credential.Kind,
            credential.Model,
            secret,
            credential.Endpoint,
            credential.NonSecretSettings,
            source,
            credential.Id);
    }

    private string? NormaliseEndpoint(LlmProvider provider, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            if (provider is LlmProvider.OpenAiCompatible or LlmProvider.AzureFoundry)
                throw new ArgumentException($"Endpoint is required for provider '{provider}'.", nameof(endpoint));
            return null;
        }

        return LlmEndpointPolicy.Validate(endpoint, options.AllowedEndpointHosts, options.AllowPrivateEndpoints).ToString();
    }

    private async Task ClearOtherDefaultsAsync(IReadOnlyList<LlmCredential> existing, LlmCredential incoming, CancellationToken cancellationToken)
    {
        foreach (var other in existing.Where(c => c.IsDefault && c.Provider == incoming.Provider && c.ProjectId == incoming.ProjectId && c.Id != incoming.Id))
        {
            await store.SaveAsync(other with { IsDefault = false }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Workspace> RequireRoleAsync(Guid workspaceId, Guid userId, WorkspaceRole minimum, CancellationToken cancellationToken)
    {
        var role = await workspaceService.GetEffectiveRoleAsync(workspaceId, userId, cancellationToken).ConfigureAwait(false);
        if (role is null)
            throw new UnauthorizedAccessException("Access denied.");
        if (role < minimum)
            throw new UnauthorizedAccessException("Only workspace admins can manage LLM credentials.");

        return await workspaceService.GetByIdAsync(workspaceId, userId, cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("Access denied.");
    }

    /// <summary>Cross-workspace ids surface as not-found, never as forbidden, so there is no existence oracle.</summary>
    private async Task<LlmCredential> GetOwnedOrThrowAsync(Guid workspaceId, Guid credentialId, CancellationToken cancellationToken)
    {
        var credential = await store.GetByIdAsync(credentialId, cancellationToken).ConfigureAwait(false);
        if (credential is null || credential.WorkspaceId != workspaceId)
            throw new KeyNotFoundException($"LLM credential '{credentialId}' not found.");
        return credential;
    }

    private Task AuditAsync(Workspace workspace, Guid actorId, string action, LlmCredential credential, CancellationToken cancellationToken)
        => auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), workspace.AccountId, actorId,
            action, "LlmCredential", credential.Id.ToString(),
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow,
            $"provider={credential.Provider};model={credential.Model};fingerprint={credential.Fingerprint}"), cancellationToken);

    private static string Fingerprint(string secret)
    {
        if (secret.Length == 0) return "none";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hash)[..16];
    }

    private static string LastFour(string secret)
        => secret.Length >= 4 ? secret[^4..] : string.Empty;
}
