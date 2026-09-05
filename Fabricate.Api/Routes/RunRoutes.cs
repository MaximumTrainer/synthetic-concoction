using Fabricate.Application.Abstractions;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;

namespace Fabricate.Api.Routes;

/// <summary>
/// #66: runs are workspace-scoped. The previous <c>GET /runs</c> listed every run in the instance to any
/// authenticated caller; there is no unscoped route any more.
///
/// <para>
/// A run belonging to another workspace is reported as <c>404</c>, never <c>403</c>: a forbidden response would
/// confirm the id exists, which is itself a disclosure across tenants.
/// </para>
/// </summary>
public static class RunRoutes
{
    public static RouteGroupBuilder MapRunRoutes(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/workspaces/{workspaceId:guid}/runs").WithTags("Runs");

        group.MapPost("/", async (
            Guid workspaceId,
            StartRunRequest req,
            IRunExecutionService runs,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            try
            {
                var run = await runs.StartAsync(
                    new StartRunCommand(
                        workspaceId,
                        req.ProjectId,
                        req.RowCounts,
                        req.Seed,
                        req.SchemaSnapshotId,
                        req.Rules,
                        req.ComplianceProfile,
                        req.Exporters),
                    ctx.GetUserId(),
                    ct).ConfigureAwait(false);

                return Results.Created($"/workspaces/{workspaceId}/runs/{run.Id}", run);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }).WithName("StartRun");

        group.MapGet("/", async (
            Guid workspaceId,
            IRunExecutionService runs,
            HttpContext ctx,
            CancellationToken ct,
            int page = 1,
            int pageSize = 20) =>
            Results.Ok(await runs.ListAsync(workspaceId, ctx.GetUserId(), page, pageSize, ct).ConfigureAwait(false)))
            .WithName("ListRuns");

        group.MapGet("/{runId:guid}", async (
            Guid workspaceId,
            Guid runId,
            IRunExecutionService runs,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var run = await runs.GetAsync(workspaceId, runId, ctx.GetUserId(), ct).ConfigureAwait(false);
            return run is null ? Results.NotFound() : Results.Ok(run);
        }).WithName("GetRun");

        group.MapPost("/{runId:guid}/cancel", async (
            Guid workspaceId,
            Guid runId,
            IRunExecutionService runs,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            try
            {
                var run = await runs.CancelAsync(workspaceId, runId, ctx.GetUserId(), ct).ConfigureAwait(false);
                return run is null ? Results.NotFound() : Results.Ok(run);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).WithName("CancelRun");

        group.MapGet("/{runId:guid}/artifacts", async (
            Guid workspaceId,
            Guid runId,
            IRunExecutionService runs,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var artifacts = await runs.ListArtifactsAsync(workspaceId, runId, ctx.GetUserId(), ct).ConfigureAwait(false);
            return artifacts is null ? Results.NotFound() : Results.Ok(artifacts);
        }).WithName("ListRunArtifacts");

        // {**name} rather than {name}: artifact names carry the exporter directory (csv/main_users.csv), and a
        // single-segment parameter would not match one.
        group.MapGet("/{runId:guid}/artifacts/{**name}", async (
            Guid workspaceId,
            Guid runId,
            string name,
            IRunExecutionService runs,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var opened = await runs.OpenArtifactAsync(workspaceId, runId, name, ctx.GetUserId(), ct).ConfigureAwait(false);
            if (opened is null) return Results.NotFound();

            var (content, descriptor) = opened.Value;
            return Results.Stream(
                content,
                descriptor.ContentType,
                fileDownloadName: Path.GetFileName(descriptor.Name),
                enableRangeProcessing: true);
        }).WithName("DownloadRunArtifact");

        return group;
    }
}

/// <param name="Exporters">Formats to write; defaults to <c>csv</c>, matching the CLI.</param>
public sealed record StartRunRequest(
    IReadOnlyDictionary<string, int> RowCounts,
    long Seed,
    Guid? ProjectId = null,
    Guid? SchemaSnapshotId = null,
    RuleConfiguration? Rules = null,
    ComplianceProfile ComplianceProfile = ComplianceProfile.Default,
    IReadOnlyList<string>? Exporters = null);
