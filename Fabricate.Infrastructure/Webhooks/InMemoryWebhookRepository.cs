using System.Collections.Concurrent;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Webhooks;

public sealed class InMemoryWebhookRepository : IWebhookRepository
{
    private readonly ConcurrentDictionary<Guid, WebhookRegistration> _webhooks = new();
    private readonly ConcurrentDictionary<Guid, WebhookDelivery> _deliveries = new();

    public Task<WebhookRegistration> SaveAsync(WebhookRegistration webhook, CancellationToken cancellationToken = default)
    {
        _webhooks[webhook.Id] = webhook;
        return Task.FromResult(webhook);
    }

    public Task<WebhookRegistration?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_webhooks.TryGetValue(id, out var w) ? w : null);

    public Task<IReadOnlyList<WebhookRegistration>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WebhookRegistration> result = _webhooks.Values
            .Where(w => w.WorkspaceId == workspaceId)
            .OrderBy(w => w.CreatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _webhooks.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task<WebhookDelivery> SaveDeliveryAsync(WebhookDelivery delivery, CancellationToken cancellationToken = default)
    {
        _deliveries[delivery.Id] = delivery;
        return Task.FromResult(delivery);
    }
}
