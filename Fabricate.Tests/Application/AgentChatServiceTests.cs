using Fabricate.Application.Abstractions;
using Fabricate.Application.Chat;
using Fabricate.Application.Governance;
using Fabricate.Application.Workspaces;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;
using Xunit;

namespace Fabricate.Tests.Application;

public sealed class AgentChatServiceTests
{
    private readonly InMemoryAuditLogRepository _auditLogRepo = new();
    private readonly IAuditLogService _auditLogService;
    private readonly WorkspaceService _workspaceService;
    private readonly InstructionVersionService _instructionService;
    private readonly InMemorySessionRepository _sessionRepo = new();
    private readonly AgentChatService _chatService;

    public AgentChatServiceTests()
    {
        _auditLogService = new AuditLogService(_auditLogRepo);
        _workspaceService = new WorkspaceService(_auditLogService);
        _instructionService = new InstructionVersionService(_workspaceService);
        _chatService = new AgentChatService(_sessionRepo, new NoOpToolRegistry(), _workspaceService, _instructionService);
    }

    private async Task<(Guid workspaceId, Guid adminUserId)> CreateWorkspaceAsync()
    {
        var adminUserId = Guid.NewGuid();
        var ws = await _workspaceService.CreateAsync(new CreateWorkspaceCommand(Guid.NewGuid(), "Test WS", adminUserId));
        return (ws.Id, adminUserId);
    }

    [Fact]
    public async Task GetComposedInstructions_WorkspaceOnly_ReturnsWorkspaceContent()
    {
        var (wsId, userId) = await CreateWorkspaceAsync();
        await _instructionService.SaveAsync(wsId, "Workspace instructions.", userId);
        var session = await _chatService.CreateSessionAsync(new CreateChatSessionCommand(wsId, null, userId, "S1"));

        var composed = await _chatService.GetComposedInstructionsAsync(session.Id);

        composed.Should().Be("Workspace instructions.");
    }

    [Fact]
    public async Task GetComposedInstructions_WorkspaceAndProject_LayersContent()
    {
        var (wsId, userId) = await CreateWorkspaceAsync();
        await _instructionService.SaveAsync(wsId, "Workspace base.", userId);
        var projectId = Guid.NewGuid();
        await _instructionService.SaveProjectInstructionAsync(projectId, "Project context.", userId);
        var session = await _chatService.CreateSessionAsync(new CreateChatSessionCommand(wsId, projectId, userId, "S1"));

        var composed = await _chatService.GetComposedInstructionsAsync(session.Id);

        composed.Should().Be("Workspace base.\n\nProject context.");
    }

    [Fact]
    public async Task GetComposedInstructions_AllThreeLayers_LayersInOrder()
    {
        var (wsId, userId) = await CreateWorkspaceAsync();
        await _instructionService.SaveAsync(wsId, "Workspace base.", userId);
        var projectId = Guid.NewGuid();
        await _instructionService.SaveProjectInstructionAsync(projectId, "Project context.", userId);
        var session = await _chatService.CreateSessionAsync(new CreateChatSessionCommand(wsId, projectId, userId, "S1"));
        await _chatService.SetInstructionOverrideAsync(session.Id, "Session override.", userId);

        var composed = await _chatService.GetComposedInstructionsAsync(session.Id);

        composed.Should().Be("Workspace base.\n\nProject context.\n\nSession override.");
    }

    [Fact]
    public async Task GetComposedInstructions_NoInstructions_ReturnsEmpty()
    {
        var (wsId, userId) = await CreateWorkspaceAsync();
        var session = await _chatService.CreateSessionAsync(new CreateChatSessionCommand(wsId, null, userId, "Empty"));

        var composed = await _chatService.GetComposedInstructionsAsync(session.Id);

        composed.Should().BeEmpty();
    }

    [Fact]
    public async Task SetInstructionOverride_PersistsAndClearsOnNull()
    {
        var (wsId, userId) = await CreateWorkspaceAsync();
        var session = await _chatService.CreateSessionAsync(new CreateChatSessionCommand(wsId, null, userId, "S"));

        var updated = await _chatService.SetInstructionOverrideAsync(session.Id, "My override", userId);
        updated.InstructionOverride.Should().Be("My override");

        var cleared = await _chatService.SetInstructionOverrideAsync(session.Id, null, userId);
        cleared.InstructionOverride.Should().BeNull();
    }

    [Fact]
    public async Task GetComposedInstructions_UnknownSession_ReturnsEmpty()
    {
        var composed = await _chatService.GetComposedInstructionsAsync(Guid.NewGuid());
        composed.Should().BeEmpty();
    }

    // ── Test doubles ─────────────────────────────────────────────────────────────

    private sealed class InMemorySessionRepository : ISessionRepository
    {
        private readonly Dictionary<Guid, ChatSession> _sessions = [];
        private readonly Dictionary<Guid, List<ChatMessage>> _messages = [];
        private readonly Dictionary<Guid, ToolInvocation> _invocations = [];

        public Task<ChatSession> SaveAsync(ChatSession session, CancellationToken ct = default)
        {
            _sessions[session.Id] = session;
            return Task.FromResult(session);
        }

        public Task<ChatSession?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(_sessions.GetValueOrDefault(id));

        public Task<ChatMessage> SaveMessageAsync(ChatMessage message, CancellationToken ct = default)
        {
            if (!_messages.TryGetValue(message.SessionId, out var list))
                _messages[message.SessionId] = list = [];
            list.Add(message);
            return Task.FromResult(message);
        }

        public Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid sessionId, int skip, int take, CancellationToken ct = default)
        {
            var msgs = _messages.TryGetValue(sessionId, out var list) ? list : [];
            return Task.FromResult<IReadOnlyList<ChatMessage>>(msgs.Skip(skip).Take(take).ToArray());
        }

        public Task<ToolInvocation> SaveInvocationAsync(ToolInvocation invocation, CancellationToken ct = default)
        {
            _invocations[invocation.Id] = invocation;
            return Task.FromResult(invocation);
        }
    }

    private sealed class NoOpToolRegistry : IToolRegistry
    {
        public void Register(ITool tool) { }
        public ITool? Resolve(string toolName) => null;
        public IReadOnlyList<string> AllowedTools(Guid workspaceId) => [];
    }
}
