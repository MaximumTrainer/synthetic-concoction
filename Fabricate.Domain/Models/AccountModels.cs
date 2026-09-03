namespace Fabricate.Domain.Models;

/// <summary>The primary tenant boundary. One account per organisation.</summary>
public sealed record Account(
    Guid Id,
    string Name,
    DateTimeOffset CreatedAt);

public enum AccountRole
{
    Member = 0,
    Owner
}

public sealed record AccountMembership(
    Guid AccountId,
    Guid UserId,
    AccountRole Role,
    DateTimeOffset JoinedAt);

public sealed record UserProfile(
    Guid UserId,
    string DisplayName,
    string Email,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt = null);

public sealed record Invitation(
    Guid Id,
    Guid AccountId,
    Guid InvitedByUserId,
    string InviteeEmail,
    string Token,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AcceptedAt = null,
    DateTimeOffset? RevokedAt = null)
{
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    public bool IsRevoked => RevokedAt.HasValue;
    public bool IsAccepted => AcceptedAt.HasValue;
    public bool IsActive => !IsExpired && !IsRevoked && !IsAccepted;
}
