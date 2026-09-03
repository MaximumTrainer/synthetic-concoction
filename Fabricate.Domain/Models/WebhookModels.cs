namespace Fabricate.Domain.Models;

/// <summary>
/// A registered webhook endpoint that receives event notifications for a workspace.
/// </summary>
public sealed record WebhookRegistration(
    Guid Id,
    Guid WorkspaceId,
    string Url,
    IReadOnlyList<string> Events,
    string? SigningSecret,
    bool IsActive,
    DateTimeOffset CreatedAt);

/// <summary>
/// A recorded delivery attempt for a webhook event.
/// </summary>
public sealed record WebhookDelivery(
    Guid Id,
    Guid WebhookId,
    string Event,
    string Payload,
    int HttpStatus,
    bool Succeeded,
    string? Error,
    DateTimeOffset DeliveredAt);
