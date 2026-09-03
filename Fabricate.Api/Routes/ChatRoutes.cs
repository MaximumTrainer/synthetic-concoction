using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Api.Routes;

public static class ChatRoutes
{
    public static RouteGroupBuilder MapChatRoutes(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/workspaces/{workspaceId:guid}/chat").WithTags("Chat");

        group.MapPost("/sessions", async (
            Guid workspaceId,
            CreateChatSessionRequest req,
            IAgentChatService chatService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var session = await chatService.CreateSessionAsync(
                new CreateChatSessionCommand(workspaceId, req.ProjectId, userId, req.Name, req.Mode), ct)
                .ConfigureAwait(false);
            return Results.Ok(session);
        }).WithName("CreateChatSession");

        group.MapPost("/sessions/{sessionId:guid}/messages", async (
            Guid workspaceId,
            Guid sessionId,
            SendMessageRequest req,
            IAgentChatService chatService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var message = await chatService.SendMessageAsync(
                new SendMessageCommand(sessionId, userId, req.Content), ct).ConfigureAwait(false);
            return Results.Ok(message);
        }).WithName("SendMessage");

        group.MapGet("/sessions/{sessionId:guid}/messages", async (
            Guid workspaceId,
            Guid sessionId,
            int pageSize,
            IAgentChatService chatService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var messages = await chatService.GetHistoryAsync(
                sessionId, userId, pageSize > 0 ? pageSize : 50, ct).ConfigureAwait(false);
            return Results.Ok(messages);
        }).WithName("GetChatHistory");

        group.MapPost("/sessions/{sessionId:guid}/archive", async (
            Guid workspaceId,
            Guid sessionId,
            IAgentChatService chatService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var session = await chatService.ArchiveSessionAsync(sessionId, userId, ct).ConfigureAwait(false);
            return Results.Ok(session);
        }).WithName("ArchiveChatSession");

        group.MapPatch("/sessions/{sessionId:guid}/mode", async (
            Guid workspaceId,
            Guid sessionId,
            ChangeModeRequest req,
            IAgentChatService chatService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var session = await chatService.ChangeMode(sessionId, req.Mode, userId, ct).ConfigureAwait(false);
            return Results.Ok(session);
        }).WithName("ChangeChatMode");

        group.MapPatch("/sessions/{sessionId:guid}/instructions", async (
            Guid workspaceId,
            Guid sessionId,
            SetInstructionOverrideRequest req,
            IAgentChatService chatService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var session = await chatService.SetInstructionOverrideAsync(sessionId, req.InstructionOverride, userId, ct).ConfigureAwait(false);
            return Results.Ok(session);
        }).WithName("SetChatInstructionOverride");

        return group;
    }
}

public sealed record CreateChatSessionRequest(string Name, Guid? ProjectId, ChatMode Mode = ChatMode.Guided);
public sealed record SendMessageRequest(string Content);
public sealed record ChangeModeRequest(ChatMode Mode);
public sealed record SetInstructionOverrideRequest(string? InstructionOverride);
