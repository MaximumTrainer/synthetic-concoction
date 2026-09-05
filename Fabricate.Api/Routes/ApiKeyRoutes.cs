using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

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
            return Results.Ok(ApiKeySummary.From(key));
        }).WithName("RevokeApiKey");

        group.MapGet("/", async (
            Guid accountId,
            IApiKeyService apiKeyService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var keys = await apiKeyService.ListAsync(accountId, userId, ct).ConfigureAwait(false);
            return Results.Ok(keys.Select(ApiKeySummary.From).ToArray());
        }).WithName("ListApiKeys");

        return group;
    }
}

/// <summary>
/// The API-key projection every read returns (#89). The domain record carries <c>HashedSecret</c>, and while a
/// hash is not the key, it is still credential material with no reason to cross the API boundary — anyone able to
/// list an account's keys would otherwise walk away with every stored hash, which is what an offline cracking
/// attempt needs. The plaintext is returned exactly once, by create, in <see cref="CreateApiKeyResponse"/>.
/// </summary>
public sealed record ApiKeySummary(
    Guid Id,
    Guid AccountId,
    string Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    bool IsRevoked,
    bool IsExpired,
    bool IsActive)
{
    public static ApiKeySummary From(ApiKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new ApiKeySummary(
            key.Id, key.AccountId, key.Name, key.Scopes, key.CreatedAt,
            key.ExpiresAt, key.LastUsedAt, key.RevokedAt,
            key.IsRevoked, key.IsExpired, key.IsActive);
    }
}

public sealed record CreateApiKeyRequest(string Name, IReadOnlyList<string> Scopes, TimeSpan? Expiry);
public sealed record CreateApiKeyResponse(
    Guid Id,
    string Name,
    string PlaintextSecret,
    IReadOnlyList<string> Scopes,
    DateTimeOffset? ExpiresAt);
