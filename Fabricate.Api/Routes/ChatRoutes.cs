using System.Text.Json;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Api.Routes;

public static class ChatRoutes
{
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web);

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

        // Runs the whole turn (model call, tool loop) and returns the user message, the reply and tool activity.
        group.MapPost("/sessions/{sessionId:guid}/messages", async (
            Guid workspaceId,
            Guid sessionId,
            SendMessageRequest req,
            IAgentChatService chatService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            var turn = await chatService.SendMessageAsync(
                new SendMessageCommand(sessionId, userId, req.Content), ct).ConfigureAwait(false);
            return Results.Ok(turn);
        }).WithName("SendMessage");

        // Server-sent events: one `event:`/`data:` pair per ChatStreamEvent, terminated by a `completed` event.
        group.MapPost("/sessions/{sessionId:guid}/messages/stream", async (
            Guid workspaceId,
            Guid sessionId,
            SendMessageRequest req,
            IAgentChatService chatService,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            var userId = ctx.GetUserId();
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            await foreach (var evt in chatService.StreamMessageAsync(new SendMessageCommand(sessionId, userId, req.Content), ct).ConfigureAwait(false))
            {
                var (name, payload) = evt switch
                {
                    ChatStreamEvent.TextDelta d => ("delta", (object)new { text = d.Text }),
                    ChatStreamEvent.ToolCallRequested t => ("tool_requested", t.Invocation),
                    ChatStreamEvent.ToolCompleted t => ("tool_completed", t.Invocation),
                    ChatStreamEvent.Notice n => ("notice", new { message = n.Message }),
                    ChatStreamEvent.Completed c => ("completed", c.Result),
                    _ => ("unknown", new { }),
                };

                await ctx.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(payload, SseJson)}\n\n", ct).ConfigureAwait(false);
                await ctx.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            }
        }).WithName("StreamMessage");

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

        group.MapGet("/sessions/{sessionId:guid}/tool-invocations", async (
            Guid workspaceId,
            Guid sessionId,
            IAgentChatService chatService,
            HttpContext ctx,
            CancellationToken ct) =>
            Results.Ok(await chatService.GetToolInvocationsAsync(sessionId, ctx.GetUserId(), ct).ConfigureAwait(false)))
            .WithName("ListToolInvocations");

        // ReviewRequired sessions park tool calls as Pending; this runs one of them.
        group.MapPost("/sessions/{sessionId:guid}/tool-invocations/{invocationId:guid}/approve", async (
            Guid workspaceId,
            Guid sessionId,
            Guid invocationId,
            IAgentChatService chatService,
            HttpContext ctx,
            CancellationToken ct) =>
            Results.Ok(await chatService.ApproveToolInvocationAsync(sessionId, invocationId, ctx.GetUserId(), ct).ConfigureAwait(false)))
            .WithName("ApproveToolInvocation");

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
