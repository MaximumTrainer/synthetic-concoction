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

/// <summary>Immutable audit log entry. Must never be updated or deleted.</summary>
public sealed record AuditEvent(
    Guid Id,
    Guid AccountId,
    Guid? ActorUserId,
    string Action,
    string? TargetType,
    string? TargetId,
    string CorrelationId,
    DateTimeOffset OccurredAt,
    string? Details = null);
