using Fabricate.Domain.Enums;

namespace Fabricate.Domain.Models;

public enum WorkspaceRole
{
    Viewer = 0,
    Editor,
    Admin
}

/// <param name="ComplianceProfile">
/// The regime the workspace's data falls under. Governs generation defaults and, since #83, what may be sent to a
/// model provider: a Healthcare or Finance workspace cannot opt in to putting sampled row values in prompts at all.
/// </param>
public sealed record Workspace(
    Guid Id,
    Guid AccountId,
    string Name,
    DateTimeOffset CreatedAt,
    ComplianceProfile ComplianceProfile = ComplianceProfile.Default);

public sealed record WorkspaceMembership(
    Guid WorkspaceId,
    Guid PrincipalId,
    bool IsGroup,
    WorkspaceRole Role,
    DateTimeOffset GrantedAt);

/// <param name="CipherText">
/// The connection string, encrypted with the same <c>ISecretCipher</c> as LLM credentials (#69). Never returned
/// by any read path — <see cref="ConnectionSummary"/> is what callers see.
/// </param>
/// <param name="Fingerprint">
/// A short hash of the connection string, so two connections can be told apart and a rotation can be seen to have
/// happened without disclosing either value.
/// </param>
/// <param name="Redacted">
/// The connection string with every credential-bearing value removed — enough to recognise which host and
/// database a connection points at, safe to show and to log.
/// </param>
public sealed record Connection(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    string Provider,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DisabledAt = null,
    string CipherText = "",
    string KeyVersion = "",
    string Fingerprint = "",
    string Redacted = "",
    DateTimeOffset? LastValidatedAt = null,
    string? LastValidationError = null)
{
    public bool IsActive => string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase) && DisabledAt is null;

    /// <summary>True once a connection string has been stored; a connection without one cannot be discovered from.</summary>
    public bool HasSecret => CipherText.Length > 0;

    public ConnectionSummary ToSummary() => new(
        Id, WorkspaceId, Name, Provider, Status, CreatedAt, DisabledAt,
        Fingerprint, Redacted, HasSecret, LastValidatedAt, LastValidationError);
}

/// <summary>
/// The projection every read returns. Carries a fingerprint and a redacted form, never the connection string —
/// which contains a password by construction (#69).
/// </summary>
public sealed record ConnectionSummary(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    string Provider,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DisabledAt,
    string Fingerprint,
    string Redacted,
    bool HasSecret,
    DateTimeOffset? LastValidatedAt,
    string? LastValidationError);

/// <summary>A named reference to a secret. The actual secret value is never stored in this model.</summary>
public sealed record SecretRef(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    DateTimeOffset CreatedAt);

/// <summary>
/// A versioned instruction layer. Workspace-scoped versions carry <see cref="WorkspaceId"/> with a null
/// <see cref="ProjectId"/>; project-scoped versions carry <see cref="ProjectId"/>, and their
/// <see cref="WorkspaceId"/> is <see cref="Guid.Empty"/> because the saving API does not receive one.
/// </summary>
public sealed record InstructionVersion(
    Guid Id,
    Guid WorkspaceId,
    int Version,
    string Content,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAt,
    Guid? ProjectId = null);
