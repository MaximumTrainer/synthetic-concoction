namespace Fabricate.Domain.Models;

public enum LlmCredentialKind
{
    /// <summary>A bearer secret (API key) presented on every call.</summary>
    ApiKey = 0,
    /// <summary>Ambient cloud identity (IAM role, ADC, managed identity). The stored secret is empty.</summary>
    CloudIdentity
}

public enum LlmCredentialStatus
{
    Active = 0,
    Invalid,
    Revoked
}

/// <summary>
/// A tenant-supplied LLM credential. <see cref="CipherText"/> holds the encrypted secret only;
/// nothing on this type may ever contain the plaintext.
/// </summary>
public sealed record LlmCredential(
    Guid Id,
    Guid WorkspaceId,
    Guid? ProjectId,
    string Name,
    LlmProvider Provider,
    LlmCredentialKind Kind,
    string CipherText,
    string KeyVersion,
    string Fingerprint,
    string LastFour,
    string? Endpoint,
    string Model,
    IReadOnlyDictionary<string, string> NonSecretSettings,
    bool IsDefault,
    LlmCredentialStatus Status,
    DateTimeOffset CreatedAt,
    Guid CreatedByUserId,
    DateTimeOffset? LastValidatedAt = null,
    DateTimeOffset? LastUsedAt = null,
    DateTimeOffset? RevokedAt = null,
    Guid? OwnerUserId = null,
    Guid? SessionId = null)
{
    public bool IsActive => Status == LlmCredentialStatus.Active && RevokedAt is null;

    /// <summary>
    /// A credential belonging to one member rather than to the workspace (#85). Only its owner may read, rotate
    /// or use it; a workspace admin sees that it exists, for governance, and nothing more.
    /// </summary>
    public bool IsPersonal => OwnerUserId is not null;

    public LlmCredentialSummary ToSummary() => new(
        Id, WorkspaceId, ProjectId, Name, Provider, Kind, Fingerprint, LastFour, Endpoint, Model,
        NonSecretSettings, IsDefault, Status, CreatedAt, LastValidatedAt, LastUsedAt, RevokedAt,
        OwnerUserId, SessionId);
}

/// <summary>The redacted projection returned by every read path. Carries a fingerprint and last four characters only.</summary>
public sealed record LlmCredentialSummary(
    Guid Id,
    Guid WorkspaceId,
    Guid? ProjectId,
    string Name,
    LlmProvider Provider,
    LlmCredentialKind Kind,
    string Fingerprint,
    string LastFour,
    string? Endpoint,
    string Model,
    IReadOnlyDictionary<string, string> NonSecretSettings,
    bool IsDefault,
    LlmCredentialStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastValidatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    Guid? OwnerUserId = null,
    Guid? SessionId = null)
{
    /// <summary>True when this credential belongs to one member rather than to the workspace (#85).</summary>
    public bool IsPersonal => OwnerUserId is not null;
}

/// <summary>
/// Per-workspace agent policy: whether the operator's platform credential may be used, and which tools the model
/// may be offered. <see cref="AllowedTools"/> null means every registered tool; an empty list means none.
/// </summary>
/// <param name="AllowPersonalCredentials">
/// Whether members may attach their own credentials to this workspace (#85). Defaults to true. A workspace that
/// must run everything through one shared, audited key sets it false, which both blocks new personal credentials
/// and makes existing ones unresolvable — turning the switch off has to take effect immediately, or it is advice
/// rather than a control.
/// </param>
/// <param name="DailyTokenBudget">
/// Tokens the workspace may consume between UTC midnights, or null for no daily cap (#77). Once exceeded the chat
/// returns a notice and makes no provider call — a budget that only warns is not a budget.
/// </param>
/// <param name="MonthlyTokenBudget">Tokens for the current UTC calendar month, or null for no monthly cap.</param>
/// <param name="AllowSampledDataInPrompts">
/// Whether tool results carrying sampled row values or profiling aggregates may enter a prompt (#83). Defaults to
/// false: schema metadata may leave the instance, the data itself may not until someone with authority says so.
/// It cannot be set at all on a Healthcare or Finance workspace — see <see cref="PromptDataBoundary"/>.
/// </param>
public sealed record WorkspaceLlmPolicy(
    Guid WorkspaceId,
    bool AllowPlatformFallback,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string>? AllowedTools = null,
    bool AllowSampledDataInPrompts = false,
    long? DailyTokenBudget = null,
    long? MonthlyTokenBudget = null,
    bool AllowPersonalCredentials = true);

public sealed record LlmCredentialValidationResult(
    Guid CredentialId,
    bool IsValid,
    string Message,
    DateTimeOffset CheckedAt);
