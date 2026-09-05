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
        // A personal credential needs only membership: it is the member's own key and their own bill. A shared
        // one still needs admin, because it spends the workspace's quota (#85).
        var workspace = await RequireRoleAsync(
            command.WorkspaceId,
            requestingUserId,
            command.IsPersonal ? WorkspaceRole.Viewer : WorkspaceRole.Admin,
            cancellationToken).ConfigureAwait(false);

        if (command.IsPersonal)
        {
            var policy = await store.GetPolicyAsync(command.WorkspaceId, cancellationToken).ConfigureAwait(false);
            if (policy is not null && !policy.AllowPersonalCredentials)
            {
                throw new InvalidOperationException(
                    "This workspace does not permit personal LLM credentials. Everything here runs through the " +
                    "workspace's shared credential; a workspace admin can change that on the LLM policy.");
            }
        }

        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ArgumentException("Credential name is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.Model))
            throw new ArgumentException("Model is required.", nameof(command));
        if (command.Kind == LlmCredentialKind.ApiKey && string.IsNullOrWhiteSpace(command.Secret))
            throw new ArgumentException("Secret is required for API-key credentials.", nameof(command));
        if (!options.IsModelAllowed(command.Model))
            throw new ArgumentException($"Model '{command.Model}' is not in the instance allowlist.", nameof(command));

        var endpoint = NormaliseEndpoint(command.Provider, command.Endpoint);

        var all = await store.ListByWorkspaceAsync(command.WorkspaceId, cancellationToken).ConfigureAwait(false);
        var owner = command.IsPersonal ? requestingUserId : (Guid?)null;

        // Names are unique within their own scope. A member naming their key "default" must not collide with
        // another member's, nor be blocked by the workspace's.
        var existing = all.Where(c => c.OwnerUserId == owner).ToArray();
        if (existing.Any(c => c.RevokedAt is null && c.Name.Equals(command.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"A credential named '{command.Name}' already exists in this scope.", nameof(command));

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
            requestingUserId,
            OwnerUserId: owner,
            SessionId: command.IsPersonal ? command.SessionId : null);

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
        var workspace = await RequireRoleAsync(workspaceId, requestingUserId, WorkspaceRole.Viewer, cancellationToken).ConfigureAwait(false);
        var credential = await GetOwnedOrThrowAsync(workspaceId, credentialId, cancellationToken).ConfigureAwait(false);
        await RequireCanManageAsync(workspace.Id, credential, requestingUserId, "rotate", cancellationToken).ConfigureAwait(false);

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
        var workspace = await RequireRoleAsync(workspaceId, requestingUserId, WorkspaceRole.Viewer, cancellationToken).ConfigureAwait(false);
        var credential = await GetOwnedOrThrowAsync(workspaceId, credentialId, cancellationToken).ConfigureAwait(false);

        // Revocation is the one management action a workspace admin keeps over a personal credential: offboarding
        // a member has to be possible without their cooperation. It destroys access rather than granting any.
        if (credential.IsPersonal && credential.OwnerUserId != requestingUserId)
        {
            await RequireRoleAsync(workspaceId, requestingUserId, WorkspaceRole.Admin, cancellationToken).ConfigureAwait(false);
        }
        else if (!credential.IsPersonal)
        {
            await RequireRoleAsync(workspaceId, requestingUserId, WorkspaceRole.Admin, cancellationToken).ConfigureAwait(false);
        }

        var revoked = credential with { Status = LlmCredentialStatus.Revoked, RevokedAt = DateTimeOffset.UtcNow, IsDefault = false };
        await store.SaveAsync(revoked, cancellationToken).ConfigureAwait(false);
        await AuditAsync(workspace, requestingUserId, "llm_credential.revoked", revoked, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LlmCredentialSummary>> ListAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var role = await RequireRoleAsync(workspaceId, requestingUserId, WorkspaceRole.Viewer, cancellationToken).ConfigureAwait(false);
        var effectiveRole = await workspaceService.GetEffectiveRoleAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        var credentials = await store.ListByWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        _ = role;

        // Shared credentials and the caller's own, always. Other members' personal credentials only for admins,
        // and only as the same redacted summary — which carries a fingerprint and last four, never the secret —
        // so governance can see that a personal key exists without being able to read or use it.
        var visible = credentials.Where(c =>
            !c.IsPersonal
            || c.OwnerUserId == requestingUserId
            || effectiveRole >= WorkspaceRole.Admin);

        return visible.OrderBy(c => c.CreatedAt).Select(c => c.ToSummary()).ToArray();
    }

    public async Task<LlmCredentialValidationResult> ValidateAsync(Guid workspaceId, Guid credentialId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var workspace = await RequireRoleAsync(workspaceId, requestingUserId, WorkspaceRole.Viewer, cancellationToken).ConfigureAwait(false);
        var credential = await GetOwnedOrThrowAsync(workspaceId, credentialId, cancellationToken).ConfigureAwait(false);

        // Validation spends the credential — it makes a real provider call — so for a personal credential it
        // counts as using it and is owner-only. For a shared one it stays open to any member, as before.
        RequireOwnerIfPersonal(credential, requestingUserId, "validate");

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

    public async Task<WorkspaceLlmPolicy> SetPolicyAsync(Guid workspaceId, bool allowPlatformFallback, Guid requestingUserId, IReadOnlyList<string>? allowedTools = null, bool? allowSampledDataInPrompts = null, long? dailyTokenBudget = null, long? monthlyTokenBudget = null, bool? allowPersonalCredentials = null, CancellationToken cancellationToken = default)
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

        // A negative value clears the cap. "Omit to leave unchanged" and "send null to clear" cannot both be
        // expressed by a nullable field, and leaving a budget in place by accident is the worse failure.
        var daily = ResolveBudget(dailyTokenBudget, existing?.DailyTokenBudget);
        var monthly = ResolveBudget(monthlyTokenBudget, existing?.MonthlyTokenBudget);

        var personal = allowPersonalCredentials ?? existing?.AllowPersonalCredentials ?? true;

        var policy = await store.SavePolicyAsync(
            new WorkspaceLlmPolicy(workspaceId, allowPlatformFallback, DateTimeOffset.UtcNow, tools, sampledData, daily, monthly, personal),
            cancellationToken).ConfigureAwait(false);

        await auditLogService.RecordAsync(new AuditEvent(
            Guid.NewGuid(), workspace.AccountId, requestingUserId,
            "llm_policy.updated", "Workspace", workspaceId.ToString(),
            Guid.NewGuid().ToString(), DateTimeOffset.UtcNow,
            $"allowPlatformFallback={allowPlatformFallback};allowedTools={(tools is null ? "all" : string.Join(",", tools))};allowSampledDataInPrompts={sampledData};dailyTokenBudget={daily?.ToString() ?? "none"};monthlyTokenBudget={monthly?.ToString() ?? "none"};allowPersonalCredentials={personal}"), cancellationToken).ConfigureAwait(false);

        return policy;
    }

    /// <summary>Omitted leaves the budget as it was; a negative value clears it; anything else sets it.</summary>
    private static long? ResolveBudget(long? requested, long? existing) => requested switch
    {
        null => existing,
        < 0 => null,
        _ => requested,
    };

    /// <summary>
    /// A personal credential may be rotated, validated or otherwise used only by its owner — not by a workspace
    /// admin (#85). A shared one still requires admin. The message says which, because "not found" here would
    /// be a lie and "forbidden" without a reason is unactionable.
    /// </summary>
    private async Task RequireCanManageAsync(
        Guid workspaceId,
        LlmCredential credential,
        Guid requestingUserId,
        string action,
        CancellationToken cancellationToken)
    {
        if (credential.IsPersonal)
        {
            RequireOwnerIfPersonal(credential, requestingUserId, action);
            return;
        }

        await RequireRoleAsync(workspaceId, requestingUserId, WorkspaceRole.Admin, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses anyone but the owner of a personal credential, and says nothing about a shared one — the caller
    /// has already applied whatever role that action needs.
    /// </summary>
    private static void RequireOwnerIfPersonal(LlmCredential credential, Guid requestingUserId, string action)
    {
        if (credential.IsPersonal && credential.OwnerUserId != requestingUserId)
        {
            throw new UnauthorizedAccessException(
                $"Only the member who owns a personal credential can {action} it. Workspace admins can see " +
                "that it exists, and revoke it, but never read or use it.");
        }
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
