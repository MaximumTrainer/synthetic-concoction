using Fabricate.Application.Abstractions;
using Fabricate.Application.Chat;
using Fabricate.Application.Governance;
using Fabricate.Application.Llm;
using Fabricate.Application.Workspaces;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;

namespace Fabricate.Tests.Application;

/// <summary>
/// #72: <c>AgentChatService</c> persisted <c>ToolInvocation</c> rows but wrote no audit event, so a blocked tool
/// call — the case #30 called out as "unauthorized tool invocations are blocked and audited" — never reached the
/// account audit log. These cover each transition and, just as importantly, what the records must not carry.
/// </summary>
public sealed class ChatToolAuditTests
{
    private const string SecretArgument = "SELECT * FROM patients WHERE ssn = '123-45-6789'";

    private readonly InMemoryAuditLogRepository _auditRepo = new();
    private readonly InMemoryWorkspaceRepository _workspaceRepo = new();
    private readonly InMemoryAccountGroupRepository _groups = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly InMemoryLlmCredentialStore _policyStore = new();
    private readonly ToolRegistry _tools = new();
    private readonly WorkspaceService _workspaces;
    private readonly AgentChatService _chat;
    private readonly Guid _accountId = Guid.NewGuid();

    public ChatToolAuditTests()
    {
        var audit = new AuditLogService(_auditRepo, new InMemoryAccountRepository());
        _workspaces = new WorkspaceService(_workspaceRepo, _groups, audit);
        _tools.Register(new EchoTool("echo"));
        _tools.Register(new EchoTool("dangerous"));

        _chat = new AgentChatService(
            _sessions, _tools, _workspaces,
            new InstructionVersionService(new InMemoryInstructionVersionRepository(), _workspaces),
            new NoCredentialResolver(), new ThrowingFactory(), new HeuristicTokenBudgetEstimator(), _policyStore,
            audit, _workspaceRepo, new LlmOptions());
    }

    private async Task<(Guid WorkspaceId, Guid UserId, ChatSession Session)> CreateSessionAsync(ChatMode mode = ChatMode.Autonomous)
    {
        var userId = Guid.NewGuid();
        var workspace = await _workspaces.CreateAsync(new CreateWorkspaceCommand(_accountId, "WS", userId));
        var session = await _chat.CreateSessionAsync(new CreateChatSessionCommand(workspace.Id, null, userId, "S", mode));
        return (workspace.Id, userId, session);
    }

    private IReadOnlyList<AuditEvent> ChatEvents()
        => _auditRepo.All.Where(e => e.Action.StartsWith("chat.tool", StringComparison.Ordinal)).ToArray();

    [Fact]
    public async Task AnAllowedToolCallIsAudited()
    {
        var (workspaceId, userId, session) = await CreateSessionAsync();

        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "/tool echo {\"x\":1}"));

        var record = ChatEvents().Should().ContainSingle().Subject;
        record.Action.Should().Be("chat.tool_invoked");
        record.AccountId.Should().Be(_accountId, "the event belongs to the workspace's account, not the workspace");
        record.ActorUserId.Should().Be(userId);
        record.TargetType.Should().Be("ToolInvocation");
        record.Details.Should().Contain("tool=echo").And.Contain($"workspace={workspaceId}").And.Contain("status=Succeeded");
    }

    [Fact]
    public async Task ATooCallOutsideTheWorkspaceAllowlistIsAuditedAsBlocked()
    {
        var (workspaceId, userId, session) = await CreateSessionAsync();
        await _policyStore.SavePolicyAsync(new WorkspaceLlmPolicy(workspaceId, false, DateTimeOffset.UtcNow, ["echo"]));

        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "/tool dangerous {}"));

        var record = ChatEvents().Should().ContainSingle().Subject;
        record.Action.Should().Be("chat.tool_blocked",
            "a call the workspace does not allow is a security event, not an ordinary failure");
        record.Details.Should().Contain("tool=dangerous").And.Contain("reason=not_allowed_in_workspace");
    }

    [Fact]
    public async Task AnUnknownToolIsAuditedAsBlocked()
    {
        var (_, userId, session) = await CreateSessionAsync();

        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "/tool no_such_tool {}"));

        ChatEvents().Should().ContainSingle().Which.Action.Should().Be("chat.tool_blocked");
    }

    [Fact]
    public async Task AnApprovedCallProducesBothARequestedAndAnApprovedEvent()
    {
        var (workspaceId, userId, session) = await CreateSessionAsync(ChatMode.ReviewRequired);

        // Park a call directly: the model loop is not involved, and this is the state approval acts on.
        var invocation = new ToolInvocation(
            Guid.NewGuid(), session.Id, null, "echo", "{}", null, ToolInvocationStatus.Pending, DateTimeOffset.UtcNow);
        await _sessions.SaveInvocationAsync(invocation);

        await _chat.ApproveToolInvocationAsync(session.Id, invocation.Id, userId);

        var actions = ChatEvents().Select(e => e.Action).ToArray();
        actions.Should().Contain("chat.tool_approved");
        actions.Should().Contain("chat.tool_invoked", "approval runs the call, and the run is audited too");

        var approved = ChatEvents().Single(e => e.Action == "chat.tool_approved");
        approved.Details.Should().Contain($"approvedBy={userId}").And.Contain($"workspace={workspaceId}");
    }

    [Fact]
    public async Task ToolAuditRecordsNeverCarryTheArgumentsOrTheOutput()
    {
        var (_, userId, session) = await CreateSessionAsync();

        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, $"/tool echo {{\"q\":\"{SecretArgument}\"}}"));

        var everything = string.Join("\n", ChatEvents().Select(e => $"{e.Action}|{e.TargetType}|{e.TargetId}|{e.Details}"));

        everything.Should().NotContain("123-45-6789",
            "tool arguments carry whatever the user or the model put in the prompt; copying them into the account " +
            "audit log would make it a second, longer-lived copy of the conversation");
        everything.Should().NotContain("patients");

        // The invocation id is recorded instead, so the payload remains reachable to anyone with the authority.
        var record = ChatEvents().Single();
        var invocation = (await _sessions.ListInvocationsAsync(session.Id)).Single();
        record.TargetId.Should().Be(invocation.Id.ToString());
        invocation.InputJson.Should().Contain("123-45-6789", "the invocation itself still holds the payload");
    }

    // ── stubs ────────────────────────────────────────────────────────────────────

    private sealed class EchoTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "echo";
        public string InputSchemaJson => "{}";

        public Task<string> ExecuteAsync(string inputJson, Guid sessionId, Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult("{\"ok\":true}");
    }

    private sealed class NoCredentialResolver : ILlmCredentialResolver
    {
        public Task<ResolvedLlmCredential?> ResolveAsync(Guid workspaceId, Guid? projectId, LlmProvider? preferredProvider = null, CancellationToken cancellationToken = default)
            => Task.FromResult<ResolvedLlmCredential?>(null);
    }

    private sealed class ThrowingFactory : IChatCompletionClientFactory
    {
        public IChatCompletionClient Create(ResolvedLlmCredential credential)
            => throw new InvalidOperationException("No model should be called in these tests.");
    }
}
