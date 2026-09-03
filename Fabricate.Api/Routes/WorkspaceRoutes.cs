using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Api.Routes;

public static class WorkspaceRoutes
{
    public static RouteGroupBuilder MapWorkspaceRoutes(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/workspaces").WithTags("Workspaces");

        group.MapPost("/", async (
            CreateWorkspaceRequest req,
            IWorkspaceService workspaceService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var workspace = await workspaceService.CreateAsync(
                new CreateWorkspaceCommand(req.AccountId, req.Name, userId), ct).ConfigureAwait(false);
            return Results.Ok(workspace);
        }).WithName("CreateWorkspace");

        group.MapGet("/{workspaceId:guid}", async (
            Guid workspaceId,
            IWorkspaceService workspaceService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var workspace = await workspaceService.GetByIdAsync(workspaceId, userId, ct).ConfigureAwait(false);
            return workspace is null ? Results.NotFound() : Results.Ok(workspace);
        }).WithName("GetWorkspace");

        group.MapPost("/{workspaceId:guid}/access", async (
            Guid workspaceId,
            GrantWorkspaceAccessRequest req,
            IWorkspaceService workspaceService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            await workspaceService.GrantAccessAsync(
                new GrantWorkspaceAccessCommand(workspaceId, req.PrincipalId, req.IsGroup, req.Role, userId), ct)
                .ConfigureAwait(false);
            return Results.NoContent();
        }).WithName("GrantWorkspaceAccess");

        group.MapDelete("/{workspaceId:guid}/access/{principalId:guid}", async (
            Guid workspaceId,
            Guid principalId,
            bool isGroup,
            IWorkspaceService workspaceService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            await workspaceService.RevokeAccessAsync(workspaceId, principalId, isGroup, userId, ct)
                .ConfigureAwait(false);
            return Results.NoContent();
        }).WithName("RevokeWorkspaceAccess");

        group.MapGet("/{workspaceId:guid}/connections", async (
            Guid workspaceId,
            IConnectionCatalogService connectionCatalog,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var connections = await connectionCatalog.ListAsync(workspaceId, userId, ct).ConfigureAwait(false);
            return Results.Ok(connections);
        }).WithName("ListConnections");

        group.MapPost("/{workspaceId:guid}/connections", async (
            Guid workspaceId,
            AddConnectionRequest req,
            IConnectionCatalogService connectionCatalog,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var connection = await connectionCatalog.AddConnectionAsync(
                workspaceId, req.Name, req.Provider, userId, ct).ConfigureAwait(false);
            return Results.Ok(connection);
        }).WithName("AddConnection");

        group.MapPost("/{workspaceId:guid}/instructions", async (
            Guid workspaceId,
            SaveInstructionRequest req,
            IInstructionVersionService instructionService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var version = await instructionService.SaveAsync(workspaceId, req.Content, userId, ct).ConfigureAwait(false);
            return Results.Ok(version);
        }).WithName("SaveWorkspaceInstruction");

        group.MapGet("/{workspaceId:guid}/instructions", async (
            Guid workspaceId,
            IInstructionVersionService instructionService,
            CancellationToken ct) =>
        {
            var latest = await instructionService.GetLatestAsync(workspaceId, ct).ConfigureAwait(false);
            return latest is null ? Results.NotFound() : Results.Ok(latest);
        }).WithName("GetWorkspaceInstruction");

        return group;
    }
}

public sealed record CreateWorkspaceRequest(Guid AccountId, string Name);
public sealed record GrantWorkspaceAccessRequest(Guid PrincipalId, bool IsGroup, WorkspaceRole Role);
public sealed record AddConnectionRequest(string Name, string Provider);
public sealed record SaveInstructionRequest(string Content);
