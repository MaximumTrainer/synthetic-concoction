using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Llm;

/// <summary>
/// Decides which credential a chat turn executes under. First match wins:
/// session-bound → owned by the requesting member → project-bound → workspace default for the provider →
/// the single active workspace credential → the operator's platform credential (only where policy allows) → none.
/// Callers are responsible for having authorised the user against the workspace already.
/// </summary>
public sealed class LlmCredentialResolver(
    ILlmCredentialStore store,
    ISecretCipher cipher,
    ISecretProvider secretProvider,
    IWorkspaceService workspaceService,
    LlmOptions options) : ILlmCredentialResolver
{
    public async Task<ResolvedLlmCredential?> ResolveAsync(
        Guid workspaceId,
        Guid? projectId,
        LlmProvider? preferredProvider = null,
        CancellationToken cancellationToken = default)
        => await ResolveAsync(workspaceId, projectId, null, null, preferredProvider, cancellationToken).ConfigureAwait(false);

    public async Task<ResolvedLlmCredential?> ResolveAsync(
        Guid workspaceId,
        Guid? projectId,
        Guid? userId,
        Guid? sessionId,
        LlmProvider? preferredProvider = null,
        CancellationToken cancellationToken = default)
    {
        var all = await store.ListByWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var active = all.Where(c => c.IsActive).ToArray();

        // The personal rungs (#85). Both are gated on the workspace policy and on the owner still having access:
        // checked at resolve time rather than cleaned up when access is revoked, because access can also be lost
        // by a group membership changing, and a cleanup that can be missed is not a control.
        if (userId is not null && await PersonalCredentialsUsableAsync(workspaceId, userId.Value, cancellationToken).ConfigureAwait(false))
        {
            var mine = active.Where(c => c.OwnerUserId == userId).ToArray();

            if (sessionId is not null)
            {
                var sessionPick = PreferProvider(mine.Where(c => c.SessionId == sessionId), preferredProvider);
                if (sessionPick is not null)
                    return LlmCredentialService.ToResolved(sessionPick, cipher, LlmCredentialSource.SessionBound);
            }

            var workspaceWide = mine.Where(c => c.SessionId is null).ToArray();
            var userPick = PreferProvider(workspaceWide.Where(c => c.IsDefault), preferredProvider)
                        ?? PreferProvider(workspaceWide, preferredProvider);
            if (userPick is not null)
                return LlmCredentialService.ToResolved(userPick, cipher, LlmCredentialSource.UserOwned);
        }

        // Below the personal rungs, only shared credentials are eligible. Without this a member's personal key
        // could be picked up as "the single active workspace credential" and spent by everyone.
        active = active.Where(c => !c.IsPersonal).ToArray();

        if (projectId is not null)
        {
            var projectScoped = active.Where(c => c.ProjectId == projectId).ToArray();
            var pick = PreferProvider(projectScoped.Where(c => c.IsDefault), preferredProvider)
                    ?? PreferProvider(projectScoped, preferredProvider);
            if (pick is not null)
                return LlmCredentialService.ToResolved(pick, cipher, LlmCredentialSource.Project);
        }

        var workspaceScoped = active.Where(c => c.ProjectId is null).ToArray();

        var defaultPick = PreferProvider(workspaceScoped.Where(c => c.IsDefault), preferredProvider);
        if (defaultPick is not null)
            return LlmCredentialService.ToResolved(defaultPick, cipher, LlmCredentialSource.WorkspaceDefault);

        if (workspaceScoped.Length == 1 && (preferredProvider is null || workspaceScoped[0].Provider == preferredProvider))
            return LlmCredentialService.ToResolved(workspaceScoped[0], cipher, LlmCredentialSource.WorkspaceSingle);

        if (await PlatformFallbackAllowedAsync(workspaceId, cancellationToken).ConfigureAwait(false))
            return await ResolvePlatformAsync(cancellationToken).ConfigureAwait(false);

        return null;
    }

    /// <summary>
    /// Whether personal credentials may be used here: the workspace policy permits them, and the owner still has
    /// access to the workspace.
    /// </summary>
    private async Task<bool> PersonalCredentialsUsableAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        var policy = await store.GetPolicyAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        if (policy is not null && !policy.AllowPersonalCredentials) return false;

        var role = await workspaceService.GetEffectiveRoleAsync(workspaceId, userId, cancellationToken).ConfigureAwait(false);
        return role is not null;
    }

    private static LlmCredential? PreferProvider(IEnumerable<LlmCredential> candidates, LlmProvider? preferred)
    {
        var list = candidates.OrderBy(c => c.CreatedAt).ToArray();
        if (preferred is not null)
        {
            var match = list.FirstOrDefault(c => c.Provider == preferred);
            if (match is not null) return match;
        }
        return list.FirstOrDefault();
    }

    private async Task<bool> PlatformFallbackAllowedAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        if (!options.IsPlatformCredentialConfigured)
            return false;

        switch (options.PlatformFallback)
        {
            case PlatformFallbackMode.Always:
                return true;
            case PlatformFallbackMode.Never:
                return false;
            default:
                var policy = await store.GetPolicyAsync(workspaceId, cancellationToken).ConfigureAwait(false);
                return policy?.AllowPlatformFallback == true;
        }
    }

    private async Task<ResolvedLlmCredential?> ResolvePlatformAsync(CancellationToken cancellationToken)
    {
        var provider = options.ParsedProvider;
        if (provider is null || string.IsNullOrWhiteSpace(options.Model))
            return null;

        var kind = provider is LlmProvider.AwsBedrock or LlmProvider.GcpVertexAi
            ? LlmCredentialKind.CloudIdentity
            : LlmCredentialKind.ApiKey;

        var secret = string.Empty;
        if (kind == LlmCredentialKind.ApiKey && !string.IsNullOrWhiteSpace(options.ApiKeySecretName))
        {
            if (!await secretProvider.ExistsAsync(options.ApiKeySecretName, cancellationToken).ConfigureAwait(false))
                return null;
            secret = await secretProvider.ResolveAsync(options.ApiKeySecretName, cancellationToken).ConfigureAwait(false);
        }

        var settings = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(options.Region)) settings["region"] = options.Region;
        if (!string.IsNullOrWhiteSpace(options.ProjectId)) settings["projectId"] = options.ProjectId;
        if (!string.IsNullOrWhiteSpace(options.Location)) settings["location"] = options.Location;

        return new ResolvedLlmCredential(provider.Value, kind, options.Model, secret, options.BaseUrl, settings, LlmCredentialSource.Platform);
    }
}
