using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Api.Routes;

/// <summary>
/// #70: ingesting OpenAPI contracts, binding their endpoints to generated data, and serving them.
///
/// <para>
/// The mock routes sit under the same authentication and rate limiting as the rest of the API, and every call is
/// audited by the usage middleware (#72) like any other request — a mock endpoint is still this instance serving
/// a tenant's data, not an open door.
/// </para>
/// </summary>
public static class GeneratedApiRoutes
{
    public static RouteGroupBuilder MapGeneratedApiRoutes(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/workspaces/{workspaceId:guid}").WithTags("GeneratedApi");

        group.MapPost("/api-contracts", async (
            Guid workspaceId,
            IngestContractRequest req,
            IGeneratedApiService api,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            try
            {
                var contract = await api
                    .IngestAsync(new IngestContractCommand(workspaceId, req.Name, req.Document), ctx.GetUserId(), ct)
                    .ConfigureAwait(false);

                return Results.Created($"/workspaces/{workspaceId}/api-contracts/{contract.Id}", contract);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (InvalidOperationException ex)
            {
                // A document that will not parse is the caller's, so it is a 400 with the parser's reasons.
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }).WithName("IngestApiContract");

        group.MapGet("/api-contracts", async (
            Guid workspaceId,
            IGeneratedApiService api,
            HttpContext ctx,
            CancellationToken ct) =>
            Results.Ok(await api.ListContractsAsync(workspaceId, ctx.GetUserId(), ct).ConfigureAwait(false)))
            .WithName("ListApiContracts");

        group.MapGet("/api-endpoints", async (
            Guid workspaceId,
            IGeneratedApiService api,
            HttpContext ctx,
            CancellationToken ct) =>
            Results.Ok(await api.ListEndpointsAsync(workspaceId, ctx.GetUserId(), ct).ConfigureAwait(false)))
            .WithName("ListGeneratedApiEndpoints");

        group.MapPatch("/api-endpoints/{endpointId:guid}", async (
            Guid workspaceId,
            Guid endpointId,
            BindEndpointRequest req,
            IGeneratedApiService api,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            try
            {
                var endpoint = await api.BindEndpointAsync(
                    workspaceId, endpointId,
                    new BindEndpointCommand(req.ArtifactRunId, req.BoundTable, req.IsActive, req.ClearBinding),
                    ctx.GetUserId(), ct).ConfigureAwait(false);

                return endpoint is null ? Results.NotFound() : Results.Ok(endpoint);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }
        }).WithName("BindGeneratedApiEndpoint");

        // Catch-all: the path after /mock/ is matched against the contract's path templates. Every verb the
        // contract can declare is routed, so an unmatched method is a 404 from us rather than a 405 from routing.
        group.MapMethods("/mock/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE"], async (
            Guid workspaceId,
            string? path,
            IGeneratedApiService api,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var response = await api
                .ServeAsync(workspaceId, ctx.Request.Method, "/" + (path ?? string.Empty), ctx.GetUserId(), ct)
                .ConfigureAwait(false);

            if (response is null) return Results.NotFound();

            // The operation id goes back in a header so a caller can tell which contract operation answered —
            // useful when several templates could plausibly have matched.
            ctx.Response.Headers["X-Fabricate-Operation"] = response.OperationId;

            return Results.Content(response.Json, "application/json", statusCode: response.StatusCode);
        }).WithName("ServeGeneratedApi");

        return group;
    }
}

public sealed record IngestContractRequest(string Name, string Document);

/// <param name="ClearBinding">Unbinds the endpoint. Distinguishes "leave alone" from "unbind".</param>
public sealed record BindEndpointRequest(
    Guid? ArtifactRunId = null,
    string? BoundTable = null,
    bool? IsActive = null,
    bool ClearBinding = false);
