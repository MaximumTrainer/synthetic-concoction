using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Api.Routes;

/// <summary>
/// #77: reads back what a workspace or account has spent, in tokens. Cost is deliberately not computed — prices
/// change and differ by platform, so a figure here would be wrong somewhere.
/// </summary>
public static class LlmUsageRoutes
{
    public static RouteGroupBuilder MapLlmUsageRoutes(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/workspaces/{workspaceId:guid}/llm-usage").WithTags("LlmUsage");

        group.MapGet("/", async (
            Guid workspaceId,
            ILlmUsageService usage,
            HttpContext ctx,
            CancellationToken ct,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            LlmUsageGrouping groupBy = LlmUsageGrouping.Model) =>
        {
            try
            {
                return Results.Ok(await usage
                    .GetWorkspaceUsageAsync(workspaceId, ctx.GetUserId(), from, to, groupBy, ct)
                    .ConfigureAwait(false));
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }
        }).WithName("GetWorkspaceLlmUsage");

        return group;
    }

    public static RouteGroupBuilder MapAccountLlmUsageRoutes(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/accounts/{accountId:guid}/llm-usage").WithTags("LlmUsage");

        group.MapGet("/", async (
            Guid accountId,
            ILlmUsageService usage,
            HttpContext ctx,
            CancellationToken ct,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            LlmUsageGrouping groupBy = LlmUsageGrouping.Model) =>
        {
            try
            {
                return Results.Ok(await usage
                    .GetAccountUsageAsync(accountId, ctx.GetUserId(), from, to, groupBy, ct)
                    .ConfigureAwait(false));
            }
            catch (UnauthorizedAccessException ex)
            {
                // The rollup spans workspaces the caller may not individually belong to, so it is owners only.
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }
        }).WithName("GetAccountLlmUsage");

        return group;
    }
}
