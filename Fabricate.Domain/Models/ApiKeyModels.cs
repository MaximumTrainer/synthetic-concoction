namespace Fabricate.Domain.Models;

/// <summary>API key entity. The <see cref="HashedSecret"/> must never contain the plaintext value.</summary>
public sealed record ApiKey(
    Guid Id,
    Guid AccountId,
    string Name,
    string HashedSecret,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt = null,
    DateTimeOffset? RevokedAt = null)
{
    public bool IsRevoked => RevokedAt.HasValue;
    public bool IsExpired => ExpiresAt.HasValue && DateTimeOffset.UtcNow >= ExpiresAt.Value;
    public bool IsActive => !IsRevoked && !IsExpired;
}

/// <summary>Scopes that an API key may be granted.</summary>
public static class ApiKeyScope
{
    public const string Read = "read";
    public const string Write = "write";
    public const string Admin = "admin";

    public static readonly IReadOnlyList<string> All = [Read, Write, Admin];
}
