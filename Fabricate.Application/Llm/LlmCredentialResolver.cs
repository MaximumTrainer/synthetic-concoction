using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Llm;

/// <summary>
/// Decides which credential a chat turn executes under. First match wins:
/// project-bound → workspace default for the provider → the single active workspace credential →
/// the operator's platform credential (only where policy allows) → none.
/// Callers are responsible for having authorised the user against the workspace already.
/// </summary>
public sealed class LlmCredentialResolver(
    ILlmCredentialStore store,
    ISecretCipher cipher,
    ISecretProvider secretProvider,
    LlmOptions options) : ILlmCredentialResolver
{
    public async Task<ResolvedLlmCredential?> ResolveAsync(Guid workspaceId, Guid? projectId, LlmProvider? preferredProvider = null, CancellationToken cancellationToken = default)
    {
        var all = await store.ListByWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var active = all.Where(c => c.IsActive).ToArray();

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
