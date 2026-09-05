using Fabricate.Domain.Models;

namespace Fabricate.Application.Llm;

/// <summary>
/// Which rung of the resolver supplied the credential, in precedence order. The two personal rungs sit above the
/// project one (#85): a member who has attached their own key means to use it, whatever the workspace also has.
/// </summary>
public enum LlmCredentialSource
{
    /// <summary>Bound to one chat session by its owner.</summary>
    SessionBound = 0,

    /// <summary>Owned by the requesting member, for this workspace.</summary>
    UserOwned,

    Project,
    WorkspaceDefault,
    WorkspaceSingle,
    Platform
}

/// <summary>
/// Short-lived carrier for a decrypted credential plus its provider settings. Never persisted, never cached
/// beyond the request. The secret is exposed through a method so it cannot land in a generated ToString().
/// </summary>
public sealed class ResolvedLlmCredential
{
    private readonly string _secret;

    public ResolvedLlmCredential(
        LlmProvider provider,
        LlmCredentialKind kind,
        string model,
        string secret,
        string? endpoint,
        IReadOnlyDictionary<string, string> settings,
        LlmCredentialSource source,
        Guid? credentialId = null)
    {
        Provider = provider;
        Kind = kind;
        Model = model;
        _secret = secret;
        Endpoint = endpoint;
        Settings = settings;
        Source = source;
        CredentialId = credentialId;
    }

    public LlmProvider Provider { get; }
    public LlmCredentialKind Kind { get; }
    public string Model { get; }
    public string? Endpoint { get; }
    public IReadOnlyDictionary<string, string> Settings { get; }
    public LlmCredentialSource Source { get; }
    public Guid? CredentialId { get; }

    public string GetSecret() => _secret;

    public string? GetSetting(string key) => Settings.TryGetValue(key, out var value) ? value : null;

    public override string ToString() => $"ResolvedLlmCredential(provider={Provider}, model={Model}, source={Source})";
}
