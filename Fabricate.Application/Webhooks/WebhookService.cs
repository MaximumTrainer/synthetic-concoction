using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Webhooks;

/// <summary>
/// Webhook registrations are workspace-scoped and carry a <see cref="WebhookRegistration.SigningSecret"/>, so every
/// operation is authorised against the caller's workspace role. Ids belonging to another workspace are reported as
/// not found rather than forbidden, so the API is not an existence oracle.
/// </summary>
public sealed class WebhookService(
    IWebhookRepository webhookRepository,
    IWorkspaceService workspaceService) : IWebhookService
{
    public async Task<WebhookRegistration> RegisterAsync(RegisterWebhookCommand command, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        await RequireEditorAsync(command.WorkspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);

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

    public async Task<WebhookRegistration?> GetAsync(Guid webhookId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var webhook = await webhookRepository.GetByIdAsync(webhookId, cancellationToken).ConfigureAwait(false);
        if (webhook is null) return null;

        var role = await workspaceService.GetEffectiveRoleAsync(webhook.WorkspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        return role.HasValue ? webhook : null;
    }

    public async Task<IReadOnlyList<WebhookRegistration>> ListAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var role = await workspaceService.GetEffectiveRoleAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (role is null) throw new UnauthorizedAccessException("Access denied to workspace.");

        return await webhookRepository.ListByWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid webhookId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var existing = await webhookRepository.GetByIdAsync(webhookId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            throw new InvalidOperationException($"Webhook '{webhookId}' not found.");

        var role = await workspaceService.GetEffectiveRoleAsync(existing.WorkspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (role is null)
            throw new InvalidOperationException($"Webhook '{webhookId}' not found.");
        if (role < WorkspaceRole.Editor)
            throw new UnauthorizedAccessException("Workspace Editor or Admin role required.");

        await webhookRepository.DeleteAsync(webhookId, cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireEditorAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        var role = await workspaceService.GetEffectiveRoleAsync(workspaceId, userId, cancellationToken).ConfigureAwait(false);
        if (role is null or < WorkspaceRole.Editor)
        {
            throw new UnauthorizedAccessException("Workspace Editor or Admin role required.");
        }
    }
}
