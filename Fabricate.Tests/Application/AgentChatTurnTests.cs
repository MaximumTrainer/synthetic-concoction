using System.Runtime.CompilerServices;
using Fabricate.Application.Abstractions;
using Fabricate.Application.Chat;
using Fabricate.Application.Governance;
using Fabricate.Application.Llm;
using Fabricate.Application.Workspaces;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;

namespace Fabricate.Tests.Application;

/// <summary>Orchestration of a chat turn against a scripted model: tool loop, modes, refusal, failure, and isolation.</summary>
public sealed class AgentChatTurnTests
{
    private readonly WorkspaceService _workspaceService;
    private readonly InstructionVersionService _instructionService;
    private readonly InMemorySessionRepository _sessionRepo = new();
    private readonly ToolRegistry _toolRegistry = new();
    private readonly ScriptedClient _client = new();
    private readonly LlmOptions _options = new() { MaxToolIterations = 3, HistoryWindow = 20 };
    private readonly HeuristicTokenBudgetEstimator _estimator = new();
    private readonly InMemoryLlmCredentialStore _policyStore = new();
    private readonly RecordingTool _echoTool = new("echo");
    private readonly AgentChatService _chat;

    public AgentChatTurnTests()
    {
        var audit = new AuditLogService(new InMemoryAuditLogRepository());
        _workspaceService = new WorkspaceService(new InMemoryWorkspaceRepository(), new InMemoryAccountGroupRepository(), audit);
        _instructionService = new InstructionVersionService(new InMemoryInstructionVersionRepository(), _workspaceService);
        _toolRegistry.Register(_echoTool);
        _toolRegistry.Register(new RecordingTool("dangerous"));

        var credential = new ResolvedLlmCredential(LlmProvider.Anthropic, LlmCredentialKind.ApiKey, "claude-opus-5", "sk-test", null,
            new Dictionary<string, string>(), LlmCredentialSource.WorkspaceDefault);

        _chat = new AgentChatService(_sessionRepo, _toolRegistry, _workspaceService, _instructionService,
            new FixedResolver(credential), new FixedFactory(_client), _estimator, _policyStore, _options);
    }

    [Fact]
    public async Task WorkspacePolicyAllowlist_RestrictsToolsOffered_AndBlocksExecution()
    {
        var (wsId, userId, session) = await CreateSessionAsync();
        await _policyStore.SavePolicyAsync(new WorkspaceLlmPolicy(wsId, false, DateTimeOffset.UtcNow, ["echo"]));
        _client.Enqueue(ToolCall("dangerous", "{}"));
        _client.Enqueue(Text("done"));

        var turn = await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "go"));

        _client.Requests[0].Tools.Select(t => t.Name).Should().Equal(["echo"], "the persisted policy narrows the registry's tools");
        turn.ToolInvocations.Should().ContainSingle().Which.Status.Should().Be(ToolInvocationStatus.Failed);
    }

    [Fact]
    public async Task WorkspacePolicyWithEmptyAllowlist_OffersNoTools()
    {
        var (wsId, userId, session) = await CreateSessionAsync();
        await _policyStore.SavePolicyAsync(new WorkspaceLlmPolicy(wsId, false, DateTimeOffset.UtcNow, []));
        _client.Enqueue(Text("ok"));

        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "hi"));

        _client.Requests[0].Tools.Should().BeEmpty();
    }

    private async Task<(Guid wsId, Guid userId, ChatSession session)> CreateSessionAsync(ChatMode mode = ChatMode.Autonomous)
    {
        var userId = Guid.NewGuid();
        var ws = await _workspaceService.CreateAsync(new CreateWorkspaceCommand(Guid.NewGuid(), "WS", userId));
        var session = await _chat.CreateSessionAsync(new CreateChatSessionCommand(ws.Id, null, userId, "S", mode));
        return (ws.Id, userId, session);
    }

    [Fact]
    public async Task PlainMessage_ProducesAssistantReplyFromModel()
    {
        var (_, userId, session) = await CreateSessionAsync();
        _client.Enqueue(Text("Hello from the model."));

        var turn = await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "hi"));

        turn.UserMessage.Role.Should().Be(MessageRole.User);
        turn.AssistantMessage.Should().NotBeNull();
        turn.AssistantMessage!.Role.Should().Be(MessageRole.Assistant);
        turn.AssistantMessage.Content.Should().Be("Hello from the model.");
        turn.StopReason.Should().Be(LlmStopReason.EndTurn);
        turn.Usage.TotalTokens.Should().Be(15);

        var history = await _chat.GetHistoryAsync(session.Id, userId);
        history.Select(m => m.Role).Should().Equal(MessageRole.User, MessageRole.Assistant);
    }

    [Fact]
    public async Task ModelRequestsTool_ToolExecutesAndResultIsFedBack()
    {
        var (_, userId, session) = await CreateSessionAsync();
        _client.Enqueue(ToolCall("echo", """{"value":42}"""));
        _client.Enqueue(Text("The tool said 42."));

        var turn = await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "call echo"));

        turn.ToolInvocations.Should().ContainSingle();
        var invocation = turn.ToolInvocations[0];
        invocation.ToolName.Should().Be("echo");
        invocation.Status.Should().Be(ToolInvocationStatus.Succeeded);
        invocation.InputJson.Should().Be("""{"value":42}""");
        invocation.OutputJson.Should().Contain("42");
        _echoTool.Calls.Should().ContainSingle().Which.userId.Should().Be(userId);

        // The second model request must carry the tool result back.
        _client.Requests.Should().HaveCount(2);
        _client.Requests[1].Messages.Should().Contain(m => m.Role == LlmMessageRole.Tool && m.ToolResult!.ToolCallId == "call-1");
        turn.AssistantMessage!.Content.Should().Be("The tool said 42.");
    }

    [Fact]
    public async Task ToolsAdvertised_AreOnlyThoseAllowedForWorkspace()
    {
        var (wsId, userId, session) = await CreateSessionAsync();
        _toolRegistry.SetAllowedTools(wsId, ["echo"]);
        _client.Enqueue(Text("ok"));

        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "hi"));

        _client.Requests[0].Tools.Select(t => t.Name).Should().Equal("echo");
    }

    [Fact]
    public async Task ModelRequestsDisallowedTool_IsRejectedWithoutExecuting()
    {
        var (wsId, userId, session) = await CreateSessionAsync();
        _toolRegistry.SetAllowedTools(wsId, ["echo"]);
        _client.Enqueue(ToolCall("dangerous", "{}"));
        _client.Enqueue(Text("done"));

        var turn = await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "go"));

        var invocation = turn.ToolInvocations.Should().ContainSingle().Subject;
        invocation.Status.Should().Be(ToolInvocationStatus.Failed);
        invocation.ErrorMessage.Should().Contain("not available");
        _client.Requests[1].Messages.Last().ToolResult!.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ReviewRequiredMode_ParksToolCallsAsPending_AndApprovalExecutesThem()
    {
        var (_, userId, session) = await CreateSessionAsync(ChatMode.ReviewRequired);
        _client.Enqueue(ToolCall("echo", """{"v":1}"""));

        var turn = await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "do it"));

        var pending = turn.ToolInvocations.Should().ContainSingle().Subject;
        pending.Status.Should().Be(ToolInvocationStatus.Pending);
        _echoTool.Calls.Should().BeEmpty();
        _client.Requests.Should().HaveCount(1, "the loop must not continue until approval");
        turn.AssistantMessage!.Content.Should().Contain("awaiting approval");

        _client.Enqueue(Text("done"));
        var approved = await _chat.ApproveToolInvocationAsync(session.Id, pending.Id, userId);

        approved.Invocation.Status.Should().Be(ToolInvocationStatus.Succeeded);
        _echoTool.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task ApprovingTheLastPendingCall_ResumesTheModelLoop_WithTheToolResult()
    {
        var (_, userId, session) = await CreateSessionAsync(ChatMode.ReviewRequired);
        _client.Enqueue(ToolCall("echo", """{"v":7}"""));
        var turn = await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "do it"));
        _client.Enqueue(Text("The tool returned 7."));

        var approval = await _chat.ApproveToolInvocationAsync(session.Id, turn.ToolInvocations[0].Id, userId);

        approval.Invocation.Status.Should().Be(ToolInvocationStatus.Succeeded);
        approval.Continuation.Should().NotBeNull("all pending calls are resolved, so the model gets the results");
        approval.Continuation!.AssistantMessage!.Content.Should().Be("The tool returned 7.");
        approval.Continuation.UserMessage.Content.Should().Be("do it");

        _client.Requests.Should().HaveCount(2);
        _client.Requests[1].Messages.Last().Role.Should().Be(LlmMessageRole.User);
        _client.Requests[1].Messages.Last().Text.Should().Contain("\"echoed\"").And.Contain("7");

        var history = await _chat.GetHistoryAsync(session.Id, userId);
        history.Select(m => m.Role).Should().EndWith([MessageRole.Tool, MessageRole.Assistant]);
    }

    [Fact]
    public async Task ApprovingOneOfSeveralPendingCalls_DoesNotResumeUntilAllAreResolved()
    {
        var (_, userId, session) = await CreateSessionAsync(ChatMode.ReviewRequired);
        _client.Enqueue(new ChatCompletionResult(null,
            [new LlmToolCall("c1", "echo", """{"v":1}"""), new LlmToolCall("c2", "echo", """{"v":2}""")],
            LlmStopReason.ToolUse, new TokenUsage(1, 1), "claude-opus-5"));
        var turn = await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "do both"));
        _client.Enqueue(Text("both done"));

        var first = await _chat.ApproveToolInvocationAsync(session.Id, turn.ToolInvocations[0].Id, userId);
        first.Continuation.Should().BeNull("one call is still pending");
        _client.Requests.Should().HaveCount(1);

        var second = await _chat.ApproveToolInvocationAsync(session.Id, turn.ToolInvocations[1].Id, userId);
        second.Continuation.Should().NotBeNull();
        second.Continuation!.AssistantMessage!.Content.Should().Be("both done");
    }

    [Fact]
    public async Task History_IsTrimmedToTheTokenBudget_DroppingOldestFirst_KeepingTheLatestUserMessage()
    {
        var (_, userId, session) = await CreateSessionAsync();
        // ~4 chars per token: each 400-char message ≈ 100 tokens. Budget leaves room for roughly two of them plus the system prompt.
        var filler = new string('x', 400);
        for (var i = 0; i < 5; i++)
        {
            _client.Enqueue(Text($"reply {i} {filler}"));
            await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, $"message {i} {filler}"));
        }
        _client.Requests.Clear();
        _options.MaxInputTokens = 450;
        _client.Enqueue(Text("final"));

        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "latest question"));

        var sent = _client.Requests.Single().Messages;
        sent.Last().Text.Should().Be("latest question", "the newest user message is never trimmed");
        sent.Should().NotContain(m => m.Text!.StartsWith("message 0"), "oldest history is dropped first");
        sent.Count.Should().BeLessThan(11);
        _estimator.Estimate(_client.Requests.Single()).Should().BeLessThanOrEqualTo(450);
    }

    [Fact]
    public async Task ApproveToolInvocation_RequiresEditorRole()
    {
        var (wsId, adminId, session) = await CreateSessionAsync(ChatMode.ReviewRequired);
        var viewerId = Guid.NewGuid();
        await _workspaceService.GrantAccessAsync(new GrantWorkspaceAccessCommand(wsId, viewerId, false, WorkspaceRole.Viewer, adminId));
        _client.Enqueue(ToolCall("echo", "{}"));
        var turn = await _chat.SendMessageAsync(new SendMessageCommand(session.Id, adminId, "do it"));

        var act = () => _chat.ApproveToolInvocationAsync(session.Id, turn.ToolInvocations[0].Id, viewerId);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        _echoTool.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolLoop_StopsAtMaxIterations()
    {
        var (_, userId, session) = await CreateSessionAsync();
        for (var i = 0; i < 10; i++) _client.Enqueue(ToolCall("echo", "{}"));

        var turn = await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "loop"));

        _client.Requests.Should().HaveCount(_options.MaxToolIterations);
        turn.ToolInvocations.Should().HaveCount(_options.MaxToolIterations);
        turn.AssistantMessage!.Content.Should().Contain("Stopped after 3 tool iterations");
    }

    [Fact]
    public async Task Refusal_IsSurfacedAsNotice_NotException()
    {
        var (_, userId, session) = await CreateSessionAsync();
        _client.Enqueue(new ChatCompletionResult(null, [], LlmStopReason.Refusal, new TokenUsage(5, 0), "claude-opus-5", "policy"));

        var turn = await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "bad"));

        turn.StopReason.Should().Be(LlmStopReason.Refusal);
        turn.AssistantMessage!.Role.Should().Be(MessageRole.System);
        turn.AssistantMessage.Content.Should().Contain("declined").And.Contain("policy");
    }

    [Fact]
    public async Task ProviderFailure_IsPersistedAsNotice_AndDoesNotThrow()
    {
        var (_, userId, session) = await CreateSessionAsync();
        _client.Fail(new LlmProviderException(LlmFailureKind.RateLimited, "Rate limited by provider."));

        var turn = await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "hi"));

        turn.StopReason.Should().Be(LlmStopReason.Error);
        turn.AssistantMessage!.Content.Should().Contain("Rate limited by provider.");
        (await _chat.GetHistoryAsync(session.Id, userId)).Should().Contain(m => m.Role == MessageRole.System);
    }

    [Fact]
    public async Task NoCredential_ReturnsGuidanceNotice_AndDirectToolCommandStillWorks()
    {
        var (_, userId, session) = await CreateSessionAsync();
        var chat = new AgentChatService(_sessionRepo, _toolRegistry, _workspaceService, _instructionService,
            new FixedResolver(null), new FixedFactory(_client), _estimator, _policyStore, _options);

        var turn = await chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "hello"));
        turn.AssistantMessage!.Content.Should().Contain("No LLM credential is configured");
        _client.Requests.Should().BeEmpty();

        var direct = await chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, """/tool echo {"x":1}"""));
        direct.ToolInvocations.Should().ContainSingle().Which.Status.Should().Be(ToolInvocationStatus.Succeeded);
    }

    [Fact]
    public async Task StreamMessage_YieldsDeltasThenCompleted()
    {
        var (_, userId, session) = await CreateSessionAsync();
        _client.Enqueue(Text("streamed reply"));

        var events = new List<ChatStreamEvent>();
        await foreach (var evt in _chat.StreamMessageAsync(new SendMessageCommand(session.Id, userId, "hi")))
            events.Add(evt);

        events.OfType<ChatStreamEvent.TextDelta>().Select(e => e.Text).Should().Equal("streamed ", "reply");
        events.Last().Should().BeOfType<ChatStreamEvent.Completed>();
    }

    [Fact]
    public async Task SystemInstructions_IncludeComposedLayersAndModeGuidance()
    {
        var (wsId, userId, session) = await CreateSessionAsync(ChatMode.Guided);
        await _instructionService.SaveAsync(wsId, "Always answer in French.", userId);
        _client.Enqueue(Text("Bonjour"));

        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "hi"));

        var system = _client.Requests[0].SystemInstructions!;
        system.Should().Contain("Always answer in French.");
        system.Should().Contain("explain what you are about to do");
        system.Should().Contain("Never ask for or repeat real production data");
    }

    [Fact]
    public async Task SamplingAndEffort_AreOmittedWhenProviderDoesNotSupportThem()
    {
        var (_, userId, session) = await CreateSessionAsync();
        _options.Effort = LlmEffort.High;
        _client.Capabilities = _client.Capabilities with { SupportsEffort = false, SupportsSampling = false };
        _client.Enqueue(Text("ok"));

        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "hi"));

        _client.Requests[0].Effort.Should().BeNull();
        _client.Requests[0].Temperature.Should().BeNull();
    }

    // ── Helpers and doubles ───────────────────────────────────────────────────────

    private static ChatCompletionResult Text(string text)
        => new(text, [], LlmStopReason.EndTurn, new TokenUsage(10, 5), "claude-opus-5");

    private static ChatCompletionResult ToolCall(string name, string args)
        => new(null, [new LlmToolCall("call-1", name, args)], LlmStopReason.ToolUse, new TokenUsage(10, 5), "claude-opus-5");

    private sealed class ScriptedClient : IChatCompletionClient
    {
        private readonly Queue<ChatCompletionResult> _responses = new();
        private LlmProviderException? _failure;

        public List<ChatCompletionRequest> Requests { get; } = [];
        public string ProviderId => "scripted";
        public ModelCapabilities Capabilities { get; set; } = new(true, true, true, true, true, 16_000);

        public void Enqueue(ChatCompletionResult result) => _responses.Enqueue(result);
        public void Fail(LlmProviderException ex) => _failure = ex;

        public Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (_failure is not null) throw _failure;
            return Task.FromResult(_responses.Dequeue());
        }

        public async IAsyncEnumerable<ChatCompletionChunk> StreamAsync(ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request);
            if (_failure is not null) throw _failure;
            var result = _responses.Dequeue();
            if (result.Text is { Length: > 0 } text)
            {
                var split = text.IndexOf(' ') + 1;
                if (split > 0 && split < text.Length)
                {
                    yield return new ChatCompletionChunk(text[..split]);
                    yield return new ChatCompletionChunk(text[split..]);
                }
                else
                {
                    yield return new ChatCompletionChunk(text);
                }
            }
            await Task.Yield();
            yield return new ChatCompletionChunk(null, result);
        }
    }

    private sealed class FixedFactory(IChatCompletionClient client) : IChatCompletionClientFactory
    {
        public IChatCompletionClient Create(ResolvedLlmCredential credential) => client;
    }

    private sealed class FixedResolver(ResolvedLlmCredential? credential) : ILlmCredentialResolver
    {
        public Task<ResolvedLlmCredential?> ResolveAsync(Guid workspaceId, Guid? projectId, LlmProvider? preferredProvider = null, CancellationToken ct = default)
            => Task.FromResult(credential);
    }

    private sealed class RecordingTool(string name) : ITool
    {
        public List<(string input, Guid userId)> Calls { get; } = [];
        public string Name => name;
        public string Description => $"Records calls to {name}.";

        public Task<string> ExecuteAsync(string inputJson, Guid sessionId, Guid userId, CancellationToken ct = default)
        {
            Calls.Add((inputJson, userId));
            return Task.FromResult($$"""{"echoed":{{inputJson}}}""");
        }
    }
}
