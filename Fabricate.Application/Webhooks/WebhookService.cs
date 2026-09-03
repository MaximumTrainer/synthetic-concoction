using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Webhooks;

public sealed class WebhookService(IWebhookRepository webhookRepository) : IWebhookService
{
    public async Task<WebhookRegistration> RegisterAsync(RegisterWebhookCommand command, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(command.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException($"Webhook URL '{command.Url}' must be an absolute HTTP/HTTPS URL.");
        }

        if (command.Events.Count == 0)
            throw new ArgumentException("At least one event type must be specified.");

        var registration = new WebhookRegistration(
            Id: Guid.NewGuid(),
            WorkspaceId: command.WorkspaceId,
            Url: command.Url,
            Events: command.Events,
            SigningSecret: command.SigningSecret,
            IsActive: true,
            CreatedAt: DateTimeOffset.UtcNow);

        return await webhookRepository.SaveAsync(registration, cancellationToken).ConfigureAwait(false);
    }

    public Task<WebhookRegistration?> GetAsync(Guid webhookId, Guid requestingUserId, CancellationToken cancellationToken = default)
        => webhookRepository.GetByIdAsync(webhookId, cancellationToken);

    public Task<IReadOnlyList<WebhookRegistration>> ListAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default)
        => webhookRepository.ListByWorkspaceAsync(workspaceId, cancellationToken);

    public async Task DeleteAsync(Guid webhookId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var existing = await webhookRepository.GetByIdAsync(webhookId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            throw new InvalidOperationException($"Webhook '{webhookId}' not found.");

        await webhookRepository.DeleteAsync(webhookId, cancellationToken).ConfigureAwait(false);
    }
}
