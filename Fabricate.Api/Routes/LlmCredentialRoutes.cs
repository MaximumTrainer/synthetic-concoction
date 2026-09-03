using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Api.Routes;

/// <summary>
/// Bring-your-own-key management (#58). Every response is an <see cref="LlmCredentialSummary"/>; the plaintext
/// travels only in the register and rotate request bodies, which are excluded from request logging.
/// </summary>
public static class LlmCredentialRoutes
{
    public const string ValidateRateLimitPolicy = "llm-validate";

    public static RouteGroupBuilder MapLlmCredentialRoutes(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/workspaces/{workspaceId:guid}/llm-credentials").WithTags("LlmCredentials");

        group.MapPost("/", async (
            Guid workspaceId,
            RegisterLlmCredentialRequest req,
            ILlmCredentialService service,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var summary = await service.RegisterAsync(
                new RegisterLlmCredentialCommand(workspaceId, req.ProjectId, req.Name, req.Provider, req.Kind, req.Secret ?? string.Empty,
                    req.Model, req.Endpoint, req.NonSecretSettings, req.IsDefault),
                ctx.GetUserId(), ct).ConfigureAwait(false);
            return Results.Created($"/workspaces/{workspaceId}/llm-credentials/{summary.Id}", summary);
        }).WithName("RegisterLlmCredential");

        group.MapGet("/", async (
            Guid workspaceId,
            ILlmCredentialService service,
            HttpContext ctx,
            CancellationToken ct) =>
            Results.Ok(await service.ListAsync(workspaceId, ctx.GetUserId(), ct).ConfigureAwait(false)))
            .WithName("ListLlmCredentials");

        group.MapPost("/{credentialId:guid}/rotate", async (
            Guid workspaceId,
            Guid credentialId,
            RotateLlmCredentialRequest req,
            ILlmCredentialService service,
            HttpContext ctx,
            CancellationToken ct) =>
            Results.Ok(await service.RotateAsync(workspaceId, credentialId, req.Secret, ctx.GetUserId(), ct).ConfigureAwait(false)))
            .WithName("RotateLlmCredential");

        group.MapPost("/{credentialId:guid}/validate", async (
            Guid workspaceId,
            Guid credentialId,
            ILlmCredentialService service,
            HttpContext ctx,
            CancellationToken ct) =>
            Results.Ok(await service.ValidateAsync(workspaceId, credentialId, ctx.GetUserId(), ct).ConfigureAwait(false)))
            .RequireRateLimiting(ValidateRateLimitPolicy)
            .WithName("ValidateLlmCredential");

        group.MapDelete("/{credentialId:guid}", async (
            Guid workspaceId,
            Guid credentialId,
            ILlmCredentialService service,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            await service.RevokeAsync(workspaceId, credentialId, ctx.GetUserId(), ct).ConfigureAwait(false);
            return Results.NoContent();
        }).WithName("RevokeLlmCredential");

        group.MapGet("/policy", async (
            Guid workspaceId,
            ILlmCredentialService service,
            HttpContext ctx,
            CancellationToken ct) =>
            Results.Ok(await service.GetPolicyAsync(workspaceId, ctx.GetUserId(), ct).ConfigureAwait(false)))
            .WithName("GetWorkspaceLlmPolicy");

        group.MapPut("/policy", async (
            Guid workspaceId,
            SetLlmPolicyRequest req,
            ILlmCredentialService service,
            HttpContext ctx,
            CancellationToken ct) =>
            Results.Ok(await service.SetPolicyAsync(workspaceId, req.AllowPlatformFallback, ctx.GetUserId(), ct).ConfigureAwait(false)))
            .WithName("SetWorkspaceLlmPolicy");

        return group;
    }
}

public sealed record RegisterLlmCredentialRequest(
    string Name,
    LlmProvider Provider,
    string Model,
    string? Secret,
    LlmCredentialKind Kind = LlmCredentialKind.ApiKey,
    string? Endpoint = null,
    Guid? ProjectId = null,
    IReadOnlyDictionary<string, string>? NonSecretSettings = null,
    bool IsDefault = false);

public sealed record RotateLlmCredentialRequest(string Secret);
public sealed record SetLlmPolicyRequest(bool AllowPlatformFallback);
