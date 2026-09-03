using Fabricate.Application.Abstractions;

namespace Fabricate.Api.Routes;

public static class ApiKeyRoutes
{
    public static RouteGroupBuilder MapApiKeyRoutes(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/accounts/{accountId:guid}/api-keys").WithTags("ApiKeys");

        group.MapPost("/", async (
            Guid accountId,
            CreateApiKeyRequest req,
            IApiKeyService apiKeyService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var (key, secret) = await apiKeyService.CreateAsync(
                new CreateApiKeyCommand(accountId, req.Name, req.Scopes, req.Expiry), userId, ct)
                .ConfigureAwait(false);
            return Results.Ok(new CreateApiKeyResponse(key.Id, key.Name, secret, key.Scopes, key.ExpiresAt));
        }).WithName("CreateApiKey");

        group.MapDelete("/{keyId:guid}", async (
            Guid accountId,
            Guid keyId,
            IApiKeyService apiKeyService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var key = await apiKeyService.RevokeAsync(keyId, userId, accountId, ct).ConfigureAwait(false);
            return Results.Ok(key);
        }).WithName("RevokeApiKey");

        group.MapGet("/", async (
            Guid accountId,
            IApiKeyService apiKeyService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var keys = await apiKeyService.ListAsync(accountId, userId, ct).ConfigureAwait(false);
            return Results.Ok(keys);
        }).WithName("ListApiKeys");

        return group;
    }
}

public sealed record CreateApiKeyRequest(string Name, IReadOnlyList<string> Scopes, TimeSpan? Expiry);
public sealed record CreateApiKeyResponse(
    Guid Id,
    string Name,
    string PlaintextSecret,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt);
