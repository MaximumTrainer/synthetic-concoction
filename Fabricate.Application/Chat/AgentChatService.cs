using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Chat;

public sealed class AgentChatService(
    ISessionRepository sessionRepository,
    IToolRegistry toolRegistry,
    IWorkspaceService workspaceService,
    IInstructionVersionService instructionVersionService) : IAgentChatService
{
    public async Task<ChatSession> CreateSessionAsync(CreateChatSessionCommand command, CancellationToken cancellationToken = default)
    {
        var role = await workspaceService.GetEffectiveRoleAsync(command.WorkspaceId, command.UserId, cancellationToken).ConfigureAwait(false);
        if (!role.HasValue)
        {
            throw new UnauthorizedAccessException("User does not have access to this workspace.");
        }

        var session = new ChatSession(Guid.NewGuid(), command.WorkspaceId, command.ProjectId, command.UserId, command.Name, command.Mode, false, DateTimeOffset.UtcNow);
        return await sessionRepository.SaveAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatSession?> GetSessionAsync(Guid sessionId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return null;

        var role = await workspaceService.GetEffectiveRoleAsync(session.WorkspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        return role.HasValue ? session : null;
    }

    public async Task<ChatSession> ArchiveSessionAsync(Guid sessionId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionOrThrowAsync(sessionId, requestingUserId, cancellationToken).ConfigureAwait(false);
        var archived = session with { IsArchived = true, ArchivedAt = DateTimeOffset.UtcNow };
        return await sessionRepository.SaveAsync(archived, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatSession> ChangeMode(Guid sessionId, ChatMode mode, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionOrThrowAsync(sessionId, requestingUserId, cancellationToken).ConfigureAwait(false);
        var updated = session with { Mode = mode };
        return await sessionRepository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatMessage> SendMessageAsync(SendMessageCommand command, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionOrThrowAsync(command.SessionId, command.UserId, cancellationToken).ConfigureAwait(false);
        if (session.IsArchived)
        {
            throw new InvalidOperationException("Cannot send messages to an archived session.");
        }

        var message = new ChatMessage(Guid.NewGuid(), command.SessionId, MessageRole.User, command.Content, DateTimeOffset.UtcNow);
        await sessionRepository.SaveMessageAsync(message, cancellationToken).ConfigureAwait(false);

        // Check if the content looks like a tool invocation request.
        if (command.Content.StartsWith("/tool ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = command.Content[6..].Split(' ', 2);
            var toolName = parts[0];
            var inputJson = parts.Length > 1 ? parts[1] : "{}";

            var tool = toolRegistry.Resolve(toolName);
            if (tool is not null)
            {
                var invocation = new ToolInvocation(Guid.NewGuid(), command.SessionId, message.Id, toolName, inputJson, null, ToolInvocationStatus.Running, DateTimeOffset.UtcNow);
                await sessionRepository.SaveInvocationAsync(invocation, cancellationToken).ConfigureAwait(false);

                string outputJson;
                ToolInvocationStatus status;
                string? error = null;
                try
                {
                    outputJson = await tool.ExecuteAsync(inputJson, command.SessionId, command.UserId, cancellationToken).ConfigureAwait(false);
                    status = ToolInvocationStatus.Succeeded;
                }
                catch (UnauthorizedAccessException ex)
                {
                    outputJson = "{}";
                    status = ToolInvocationStatus.Failed;
                    error = ex.Message;
                }

                var completed = invocation with { OutputJson = outputJson, Status = status, CompletedAt = DateTimeOffset.UtcNow, ErrorMessage = error };
                await sessionRepository.SaveInvocationAsync(completed, cancellationToken).ConfigureAwait(false);

                var assistantMessage = new ChatMessage(Guid.NewGuid(), command.SessionId, MessageRole.Tool, outputJson, DateTimeOffset.UtcNow);
                await sessionRepository.SaveMessageAsync(assistantMessage, cancellationToken).ConfigureAwait(false);
            }
        }

        return message;
    }

    public async Task<IReadOnlyList<ChatMessage>> GetHistoryAsync(Guid sessionId, Guid requestingUserId, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        await GetSessionOrThrowAsync(sessionId, requestingUserId, cancellationToken).ConfigureAwait(false);
        return await sessionRepository.GetMessagesAsync(sessionId, 0, pageSize, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChatSession> SetInstructionOverrideAsync(Guid sessionId, string? instructionOverride, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionOrThrowAsync(sessionId, requestingUserId, cancellationToken).ConfigureAwait(false);
        var updated = session with { InstructionOverride = instructionOverride };
        return await sessionRepository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetComposedInstructionsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null) return string.Empty;

        var parts = new List<string>();

        // Layer 1: workspace-level instructions (base context)
        var workspaceInstruction = await instructionVersionService.GetLatestAsync(session.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (workspaceInstruction is not null)
            parts.Add(workspaceInstruction.Content);

        // Layer 2: project-level instructions
        if (session.ProjectId.HasValue)
        {
            var projectInstruction = await instructionVersionService
                .GetLatestProjectInstructionAsync(session.ProjectId.Value, cancellationToken).ConfigureAwait(false);
            if (projectInstruction is not null)
                parts.Add(projectInstruction.Content);
        }

        // Layer 3: session-level override (highest precedence)
        if (!string.IsNullOrWhiteSpace(session.InstructionOverride))
            parts.Add(session.InstructionOverride);

        return string.Join("\n\n", parts);
    }

    private async Task<ChatSession> GetSessionOrThrowAsync(Guid sessionId, Guid requestingUserId, CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(sessionId, requestingUserId, cancellationToken).ConfigureAwait(false);
        return session ?? throw new InvalidOperationException($"Session '{sessionId}' not found or access denied.");
    }
}
