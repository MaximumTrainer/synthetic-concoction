using Fabricate.Application.Abstractions;

namespace Fabricate.Api.Routes;

public static class WorkflowRoutes
{
    public static RouteGroupBuilder MapWorkflowRoutes(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/workspaces/{workspaceId:guid}/workflows").WithTags("Workflows");

        group.MapPost("/", async (
            Guid workspaceId,
            CreateWorkflowRequest req,
            IWorkflowService workflowService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var steps = req.Steps
                .Select(s => new WorkflowStepDefinition(s.StepOrder, s.StepType, s.Configuration))
                .ToList();
            var workflow = await workflowService.CreateAsync(
                new CreateWorkflowCommand(workspaceId, req.Name, steps), userId, ct).ConfigureAwait(false);
            return Results.Ok(workflow);
        }).WithName("CreateWorkflow");

        group.MapPost("/{workflowId:guid}/runs", async (
            Guid workspaceId,
            Guid workflowId,
            IWorkflowService workflowService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var run = await workflowService.RunAsync(workflowId, userId, ct).ConfigureAwait(false);
            return Results.Ok(run);
        }).WithName("RunWorkflow");

        group.MapGet("/{workflowId:guid}/runs/{runId:guid}", async (
            Guid workspaceId,
            Guid workflowId,
            Guid runId,
            IWorkflowService workflowService,
            CancellationToken ct) =>
        {
            var run = await workflowService.GetRunAsync(runId, ct).ConfigureAwait(false);
            return run is null ? Results.NotFound() : Results.Ok(run);
        }).WithName("GetWorkflowRunStatus");

        group.MapGet("/{workflowId:guid}/runs/{runId:guid}/steps", async (
            Guid workspaceId,
            Guid workflowId,
            Guid runId,
            IWorkflowService workflowService,
            CancellationToken ct) =>
        {
            var steps = await workflowService.GetStepRunsAsync(runId, ct).ConfigureAwait(false);
            return Results.Ok(steps);
        }).WithName("GetWorkflowStepRuns");

        group.MapPost("/{workflowId:guid}/disable", async (
            Guid workspaceId,
            Guid workflowId,
            IWorkflowService workflowService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var workflow = await workflowService.DisableAsync(workflowId, userId, ct).ConfigureAwait(false);
            return Results.Ok(workflow);
        }).WithName("DisableWorkflow");

        return group;
    }
}

public sealed record CreateWorkflowRequest(string Name, IReadOnlyList<WorkflowStepRequest> Steps);
public sealed record WorkflowStepRequest(int StepOrder, string StepType, string? Configuration);
