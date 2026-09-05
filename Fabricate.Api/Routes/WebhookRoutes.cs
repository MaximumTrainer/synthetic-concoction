using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Api.Routes;

public static class WebhookRoutes
{
    public static RouteGroupBuilder MapWebhookRoutes(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/workspaces/{workspaceId:guid}/webhooks").WithTags("Webhooks");

        group.MapPost("/", async (
            Guid workspaceId,
            RegisterWebhookRequest req,
            IWebhookService webhookService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var webhook = await webhookService.RegisterAsync(
                new RegisterWebhookCommand(workspaceId, req.Url, req.Events, req.SigningSecret),
                userId, ct).ConfigureAwait(false);
            return Results.Ok(webhook);
        }).WithName("RegisterWebhook");

        group.MapGet("/", async (
            Guid workspaceId,
            IWebhookService webhookService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var webhooks = await webhookService.ListAsync(workspaceId, userId, ct).ConfigureAwait(false);
            return Results.Ok(webhooks.Select(WebhookSummary.From).ToArray());
        }).WithName("ListWebhooks");

        group.MapGet("/{webhookId:guid}", async (
            Guid workspaceId,
            Guid webhookId,
            IWebhookService webhookService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var webhook = await webhookService.GetAsync(webhookId, userId, ct).ConfigureAwait(false);
            return webhook is null ? Results.NotFound() : Results.Ok(WebhookSummary.From(webhook));
        }).WithName("GetWebhook");

        group.MapDelete("/{webhookId:guid}", async (
            Guid workspaceId,
            Guid webhookId,
            IWebhookService webhookService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            await webhookService.DeleteAsync(webhookId, userId, ct).ConfigureAwait(false);
            return Results.NoContent();
        }).WithName("DeleteWebhook");

        return group;
    }
}

/// <summary>
/// The webhook projection reads return (#89). <c>SigningSecret</c> is the shared secret a receiver verifies
/// signatures with; it is echoed once by registration, to the caller who supplied or requested it, and never
/// again. Listing every workspace webhook must not hand out the secrets along with them.
/// </summary>
public sealed record WebhookSummary(
    Guid Id,
    Guid WorkspaceId,
    string Url,
    IReadOnlyList<string> Events,
    bool HasSigningSecret,
    bool IsActive,
    DateTimeOffset CreatedAt)
{
    public static WebhookSummary From(WebhookRegistration webhook)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        return new WebhookSummary(
            webhook.Id, webhook.WorkspaceId, webhook.Url, webhook.Events,
            !string.IsNullOrEmpty(webhook.SigningSecret), webhook.IsActive, webhook.CreatedAt);
    }
}

public sealed record RegisterWebhookRequest(string Url, IReadOnlyList<string> Events, string? SigningSecret = null);
