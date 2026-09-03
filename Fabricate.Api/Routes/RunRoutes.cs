using Fabricate.Application.Generation;

namespace Fabricate.Api.Routes;

public static class RunRoutes
{
    public static RouteGroupBuilder MapRunRoutes(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/runs").WithTags("Runs");

        group.MapGet("/", async (
            int page,
            int pageSize,
            RunLifecycleService runService,
            CancellationToken ct) =>
        {
            var runs = await runService.ListRunsAsync(pageSize > 0 ? pageSize : 20, page > 0 ? page : 1, ct)
                .ConfigureAwait(false);
            return Results.Ok(runs);
        }).WithName("ListRuns");

        group.MapGet("/{runId:guid}", async (
            Guid runId,
            RunLifecycleService runService,
            CancellationToken ct) =>
        {
            var run = await runService.GetRunAsync(runId, ct).ConfigureAwait(false);
            return run is null ? Results.NotFound() : Results.Ok(run);
        }).WithName("GetRun");

        group.MapPost("/{runId:guid}/cancel", async (
            Guid runId,
            RunLifecycleService runService,
            CancellationToken ct) =>
        {
            try
            {
                var run = await runService.CancelRunAsync(runId, ct).ConfigureAwait(false);
                return Results.Ok(run);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).WithName("CancelRun");

        return group;
    }
}
