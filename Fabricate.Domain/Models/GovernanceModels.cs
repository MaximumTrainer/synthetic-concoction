namespace Fabricate.Domain.Models;

public sealed record AccountGroup(
    Guid Id,
    Guid AccountId,
    string Name,
    DateTimeOffset CreatedAt);

public sealed record GroupMembership(
    Guid GroupId,
    Guid UserId,
    DateTimeOffset JoinedAt);

public sealed record AllowedDomain(
    Guid Id,
    Guid AccountId,
    string Domain,
    DateTimeOffset CreatedAt);

/// <summary>
/// Immutable audit log entry. Never updated; deleted only by the retention sweep (#74).
/// </summary>
/// <param name="ApiKeyId">
/// The API key the request authenticated with, where one did (#72). Its own column rather than a detail string
/// so "everything this key did" is an indexed query rather than a scan.
/// </param>
public sealed record AuditEvent(
    Guid Id,
    Guid AccountId,
    Guid? ActorUserId,
    string Action,
    string? TargetType,
    string? TargetId,
    string CorrelationId,
    DateTimeOffset OccurredAt,
    string? Details = null,
    Guid? ApiKeyId = null);
