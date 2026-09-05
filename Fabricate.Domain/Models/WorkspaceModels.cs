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

public sealed record Connection(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    string Provider,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DisabledAt = null);

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
