using Fabricate.Application.Abstractions;
using Fabricate.Domain.Enums;
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
                new CreateWorkspaceCommand(req.AccountId, req.Name, userId, req.ComplianceProfile), ct).ConfigureAwait(false);
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

        // The connection string is accepted once here and never returned by any read path (#69).
        group.MapPost("/{workspaceId:guid}/connections", async (
            Guid workspaceId,
            AddConnectionRequest req,
            IConnectionCatalogService connectionCatalog,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            try
            {
                var connection = await connectionCatalog.AddConnectionAsync(
                    workspaceId, req.Name, req.Provider, ctx.GetUserId(), req.ConnectionString, ct).ConfigureAwait(false);
                return Results.Created($"/workspaces/{workspaceId}/connections/{connection.Id}", connection);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }).WithName("AddConnection");

        group.MapGet("/{workspaceId:guid}/connections/{connectionId:guid}", async (
            Guid workspaceId,
            Guid connectionId,
            IConnectionCatalogService connectionCatalog,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var connection = await connectionCatalog.GetAsync(workspaceId, connectionId, ctx.GetUserId(), ct).ConfigureAwait(false);
            return connection is null ? Results.NotFound() : Results.Ok(connection);
        }).WithName("GetConnection");

        group.MapPost("/{workspaceId:guid}/connections/{connectionId:guid}/rotate", async (
            Guid workspaceId,
            Guid connectionId,
            RotateConnectionRequest req,
            IConnectionCatalogService connectionCatalog,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await connectionCatalog
                    .RotateAsync(workspaceId, connectionId, req.ConnectionString, ctx.GetUserId(), ct)
                    .ConfigureAwait(false));
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
        }).WithName("RotateConnection");

        group.MapPost("/{workspaceId:guid}/connections/{connectionId:guid}/validate", async (
            Guid workspaceId,
            Guid connectionId,
            IConnectionCatalogService connectionCatalog,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await connectionCatalog
                    .ValidateAsync(workspaceId, connectionId, ctx.GetUserId(), ct)
                    .ConfigureAwait(false));
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).WithName("ValidateConnection");

        group.MapDelete("/{workspaceId:guid}/connections/{connectionId:guid}", async (
            Guid workspaceId,
            Guid connectionId,
            IConnectionCatalogService connectionCatalog,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            try
            {
                await connectionCatalog.RemoveConnectionAsync(connectionId, ctx.GetUserId(), workspaceId, ct).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }
        }).WithName("RemoveConnection");

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

/// <param name="ComplianceProfile">
/// Fixed at creation. Healthcare and Finance workspaces cannot opt in to sending sampled data to a model
/// provider at all (#83), so this is a decision about the data, not a preference.
/// </param>
public sealed record CreateWorkspaceRequest(
    Guid AccountId,
    string Name,
    ComplianceProfile ComplianceProfile = ComplianceProfile.Default);
public sealed record GrantWorkspaceAccessRequest(Guid PrincipalId, bool IsGroup, WorkspaceRole Role);
/// <param name="ConnectionString">
/// Sent once, at creation, and never returned. Reads produce a summary carrying a fingerprint and a redacted
/// form instead (#69).
/// </param>
public sealed record AddConnectionRequest(string Name, string Provider, string? ConnectionString = null);

public sealed record RotateConnectionRequest(string ConnectionString);
public sealed record SaveInstructionRequest(string Content);
