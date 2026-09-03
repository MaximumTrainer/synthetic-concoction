namespace Fabricate.Domain.Models;

public enum ProjectDatabaseType
{
    Local = 0,
    External
}

public enum ProjectStatus
{
    Active = 0,
    Archived
}

public sealed record Project(
    Guid Id,
    Guid WorkspaceId,
    string Name,
    ProjectStatus Status,
    Guid CreatedByUserId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt = null);

public sealed record ProjectDatabase(
    Guid Id,
    Guid ProjectId,
    string Name,
    ProjectDatabaseType Type,
    string Provider,
    string Status,
    Guid? ConnectionRefId,
    DateTimeOffset CreatedAt);
