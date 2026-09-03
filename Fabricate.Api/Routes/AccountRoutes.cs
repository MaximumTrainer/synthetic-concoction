using Fabricate.Application.Abstractions;

namespace Fabricate.Api.Routes;

public static class AccountRoutes
{
    public static RouteGroupBuilder MapAccountRoutes(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/accounts").WithTags("Accounts");

        group.MapPost("/", async (
            CreateAccountRequest req,
            IAccountService accountService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var account = await accountService.CreateAccountAsync(
                new CreateAccountCommand(req.Name, userId), ct).ConfigureAwait(false);
            return Results.Ok(account);
        }).WithName("CreateAccount");

        group.MapGet("/{accountId:guid}", async (
            Guid accountId,
            IAccountService accountService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var account = await accountService.GetByIdAsync(accountId, ct).ConfigureAwait(false);
            return account is null ? Results.NotFound() : Results.Ok(account);
        }).WithName("GetAccount");

        group.MapGet("/{accountId:guid}/members", async (
            Guid accountId,
            IAccountService accountService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var members = await accountService.GetMembersAsync(accountId, ct).ConfigureAwait(false);
            return Results.Ok(members);
        }).WithName("GetAccountMembers");

        group.MapPost("/{accountId:guid}/invitations", async (
            Guid accountId,
            InviteUserRequest req,
            IInvitationService invitationService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var invitation = await invitationService.InviteAsync(
                new InviteUserCommand(accountId, userId, req.InviteeEmail, req.Expiry), ct)
                .ConfigureAwait(false);
            return Results.Ok(invitation);
        }).WithName("InviteUser");

        group.MapPost("/invitations/accept", async (
            AcceptInvitationRequest req,
            IInvitationService invitationService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var membership = await invitationService.AcceptAsync(
                new AcceptInvitationCommand(req.Token, userId), ct).ConfigureAwait(false);
            return Results.Ok(membership);
        }).WithName("AcceptInvitation");

        return group;
    }
}

public sealed record CreateAccountRequest(string Name);
public sealed record InviteUserRequest(string InviteeEmail, TimeSpan Expiry);
public sealed record AcceptInvitationRequest(string Token);
