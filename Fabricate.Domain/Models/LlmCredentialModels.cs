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
    DateTimeOffset? RevokedAt = null)
{
    public bool IsActive => Status == LlmCredentialStatus.Active && RevokedAt is null;

    public LlmCredentialSummary ToSummary() => new(
        Id, WorkspaceId, ProjectId, Name, Provider, Kind, Fingerprint, LastFour, Endpoint, Model,
        NonSecretSettings, IsDefault, Status, CreatedAt, LastValidatedAt, LastUsedAt, RevokedAt);
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
    DateTimeOffset? RevokedAt);

/// <summary>
/// Per-workspace agent policy: whether the operator's platform credential may be used, and which tools the model
/// may be offered. <see cref="AllowedTools"/> null means every registered tool; an empty list means none.
/// </summary>
public sealed record WorkspaceLlmPolicy(
    Guid WorkspaceId,
    bool AllowPlatformFallback,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string>? AllowedTools = null);

public sealed record LlmCredentialValidationResult(
    Guid CredentialId,
    bool IsValid,
    string Message,
    DateTimeOffset CheckedAt);
