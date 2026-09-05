using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Api.Routes;

/// <summary>
/// #75: <c>SchemaSnapshotService</c> and <c>ProfileSnapshotService</c> existed but no route reached them, so
/// "profile versions are selectable for plan creation" (#13) and "the effective plan is reviewable before a run"
/// (#14) were only true in code.
/// </summary>
public static class SnapshotRoutes
{
    public static RouteGroupBuilder MapSnapshotRoutes(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/workspaces/{workspaceId:guid}").WithTags("Snapshots");

        // ── schema snapshots ─────────────────────────────────────────────────────

        group.MapPost("/schema-snapshots", async (
            Guid workspaceId,
            CaptureSchemaSnapshotRequest req,
            ISchemaSnapshotService snapshots,
            ISchemaDiscoveryService discovery,
            IWorkspaceService workspaces,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (await workspaces.GetEffectiveRoleAsync(workspaceId, ctx.GetUserId(), ct).ConfigureAwait(false) is not >= WorkspaceRole.Editor)
            {
                return Results.Problem("Workspace editors or admins can capture snapshots.", statusCode: StatusCodes.Status403Forbidden);
            }

            // Taking a snapshot means reading the configured source once. Per-connection discovery is #69; until
            // then this captures from the instance's own configured database.
            var schema = req.Schema ?? await discovery.DiscoverAsync(ct).ConfigureAwait(false);
            var snapshot = await snapshots.SaveSnapshotAsync(workspaceId, schema, ct).ConfigureAwait(false);

            return Results.Created($"/workspaces/{workspaceId}/schema-snapshots/{snapshot.Id}", snapshot);
        }).WithName("CaptureSchemaSnapshot");

        group.MapGet("/schema-snapshots", async (
            Guid workspaceId,
            ISchemaSnapshotService snapshots,
            IWorkspaceService workspaces,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (await workspaces.GetEffectiveRoleAsync(workspaceId, ctx.GetUserId(), ct).ConfigureAwait(false) is null)
                return Results.NotFound();

            // Summaries only: a list should not carry every stored schema in full.
            var listed = await snapshots.ListSnapshotsAsync(workspaceId, ct).ConfigureAwait(false);
            return Results.Ok(listed.Select(SchemaSnapshotSummary.From).ToArray());
        }).WithName("ListSchemaSnapshots");

        group.MapGet("/schema-snapshots/{snapshotId:guid}", async (
            Guid workspaceId,
            Guid snapshotId,
            ISchemaSnapshotService snapshots,
            IWorkspaceService workspaces,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (await workspaces.GetEffectiveRoleAsync(workspaceId, ctx.GetUserId(), ct).ConfigureAwait(false) is null)
                return Results.NotFound();

            var snapshot = await snapshots.GetSnapshotAsync(workspaceId, snapshotId, ct).ConfigureAwait(false);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        }).WithName("GetSchemaSnapshot");

        // ── profile snapshots ────────────────────────────────────────────────────

        group.MapPost("/profile-snapshots", async (
            Guid workspaceId,
            CaptureProfileSnapshotRequest req,
            IProfileSnapshotService profiles,
            ISchemaSnapshotService snapshots,
            IDataProfiler profiler,
            IWorkspaceService workspaces,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (await workspaces.GetEffectiveRoleAsync(workspaceId, ctx.GetUserId(), ct).ConfigureAwait(false) is not >= WorkspaceRole.Editor)
            {
                return Results.Problem("Workspace editors or admins can capture snapshots.", statusCode: StatusCodes.Status403Forbidden);
            }

            var schema = await snapshots.RestoreSchemaAsync(workspaceId, req.SchemaSnapshotId, ct).ConfigureAwait(false);
            if (schema is null) return Results.NotFound();

            var profile = await profiler.ProfileAsync(schema, ct).ConfigureAwait(false);
            var saved = await profiles.SaveProfileAsync(workspaceId, profile, ct).ConfigureAwait(false);

            return Results.Created($"/workspaces/{workspaceId}/profile-snapshots/{saved.Id}", saved);
        }).WithName("CaptureProfileSnapshot");

        group.MapGet("/profile-snapshots", async (
            Guid workspaceId,
            IProfileSnapshotService profiles,
            IWorkspaceService workspaces,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (await workspaces.GetEffectiveRoleAsync(workspaceId, ctx.GetUserId(), ct).ConfigureAwait(false) is null)
                return Results.NotFound();

            var listed = await profiles.ListProfilesAsync(workspaceId, ct).ConfigureAwait(false);
            return Results.Ok(listed.Select(ProfileSnapshotSummary.From).ToArray());
        }).WithName("ListProfileSnapshots");

        group.MapGet("/profile-snapshots/{profileId:guid}", async (
            Guid workspaceId,
            Guid profileId,
            IProfileSnapshotService profiles,
            IWorkspaceService workspaces,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (await workspaces.GetEffectiveRoleAsync(workspaceId, ctx.GetUserId(), ct).ConfigureAwait(false) is null)
                return Results.NotFound();

            var profile = await profiles.GetProfileAsync(workspaceId, profileId, ct).ConfigureAwait(false);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        }).WithName("GetProfileSnapshot");

        // ── plans ────────────────────────────────────────────────────────────────

        group.MapGet("/plans", async (
            Guid workspaceId,
            Guid schemaSnapshotId,
            ISchemaSnapshotService snapshots,
            IGenerationPlanService planService,
            IWorkspaceService workspaces,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (await workspaces.GetEffectiveRoleAsync(workspaceId, ctx.GetUserId(), ct).ConfigureAwait(false) is null)
                return Results.NotFound();

            var schema = await snapshots.RestoreSchemaAsync(workspaceId, schemaSnapshotId, ct).ConfigureAwait(false);
            if (schema is null) return Results.NotFound();

            // Reviewing a plan generates nothing and touches no database: it reports which strategy each column
            // would resolve to and why, so the answer can be checked before a run spends anything (#14).
            return Results.Ok(planService.BuildDiagnosticsReport(schema));
        }).WithName("GetGenerationPlan");

        return group;
    }
}

public sealed record CaptureSchemaSnapshotRequest(DatabaseSchema? Schema = null);

public sealed record CaptureProfileSnapshotRequest(Guid SchemaSnapshotId);

/// <summary>A schema snapshot without its schema, so a listing stays small.</summary>
public sealed record SchemaSnapshotSummary(
    Guid Id,
    Guid WorkspaceId,
    string DatabaseName,
    int Version,
    DateTimeOffset CapturedAt,
    int TableCount)
{
    public static SchemaSnapshotSummary From(SchemaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new SchemaSnapshotSummary(
            snapshot.Id, snapshot.WorkspaceId, snapshot.DatabaseName, snapshot.Version,
            snapshot.CapturedAt, snapshot.Schema.Tables.Count);
    }
}

/// <summary>A profile snapshot without its per-table statistics.</summary>
public sealed record ProfileSnapshotSummary(
    Guid Id,
    Guid WorkspaceId,
    string DatabaseName,
    int Version,
    DateTimeOffset CapturedAt,
    int TableCount)
{
    public static ProfileSnapshotSummary From(ProfileSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new ProfileSnapshotSummary(
            snapshot.Id, snapshot.WorkspaceId, snapshot.DatabaseName, snapshot.Version,
            snapshot.CapturedAt, snapshot.Tables.Count);
    }
}
