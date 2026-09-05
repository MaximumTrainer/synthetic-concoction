using System.Text.Json;
using Fabricate.Application.Abstractions;
using Fabricate.Application.Chat;
using Fabricate.Application.Chat.Tools;
using Fabricate.Application.Governance;
using Fabricate.Application.Llm;
using Fabricate.Application.Workspaces;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;

namespace Fabricate.Tests.Application;

/// <summary>
/// #87: the eval suite for "the agent asks clarifying questions when needed" and "plan revisions are auditable".
///
/// <para>
/// <strong>What this can and cannot check.</strong> Whether a model actually asks rather than guesses is the
/// model's judgement, and no scripted client can test it — scripting the answer would test the script. What is
/// testable, and what these do, is the two halves either side of that judgement: that the <em>prompt</em> the
/// provider receives carries the guidance for the session's mode, and that the <em>harness</em> handles each
/// shape of reply correctly — a question runs no tool, a plan is recorded and audited, a revision is recorded as
/// a revision. Live behavioural verification against a real provider is <c>AgentClarificationLiveEvalTests</c>,
/// which drives the same fixtures through a real model (#91).
/// </para>
///
/// <para>
/// Fixtures live in <see cref="Fixtures"/> so the prompt can be tuned without rewriting tests: change the
/// guidance in <see cref="AgentPromptGuidance"/> and only the fixture's expectation moves.
/// </para>
/// </summary>
public sealed class AgentClarificationEvalTests
{
    /// <summary>
    /// One case: a prompt, the mode it is asked in, and what the guidance must cover for it. The prompts are
    /// documentation — they say what kind of request the clause exists for.
    /// </summary>
    /// <param name="ExpectAskGuidance">Whether the prompt for this mode must tell the model to ask.</param>
    /// <param name="MustMention">The clause the prompt must carry, which is what this suite can check offline.</param>
    /// <param name="ExpectsQuestion">
    /// What a model should actually <em>do</em> with this prompt: ask before acting, or proceed. Nothing offline
    /// can check that — it is the model's judgement — so it is checked by the live eval in
    /// <c>AgentClarificationLiveEvalTests</c>, which reads it from here so the fixture stays the one place a case
    /// is described (#91).
    /// </param>
    public sealed record Fixture(
        string Name, string Prompt, ChatMode Mode, bool ExpectAskGuidance, string MustMention, bool ExpectsQuestion);

    public static TheoryData<Fixture> Fixtures =>
    [
        new("ambiguous-row-counts", "generate some test data", ChatMode.Guided, true,
            "how many rows", ExpectsQuestion: true),
        new("ambiguous-connection", "discover the schema and generate data", ChatMode.Guided, true,
            "which connection or project database", ExpectsQuestion: true),
        new("ambiguous-compliance", "generate patient records for testing", ChatMode.Guided, true,
            "compliance profile", ExpectsQuestion: true),
        new("destructive", "regenerate everything in the orders database", ChatMode.Guided, true,
            "overwrite or replace existing data", ExpectsQuestion: true),
        new("specific", "generate 500 rows in main.users using seed 4242", ChatMode.Guided, true,
            "proceed without asking", ExpectsQuestion: false),
        new("autonomous-assumes", "generate some test data", ChatMode.Autonomous, false,
            "state every assumption", ExpectsQuestion: false),
        new("review-required-asks", "generate some test data", ChatMode.ReviewRequired, true,
            "ask rather than park a call", ExpectsQuestion: true),
    ];

    private readonly InMemorySessionRepository _sessions = new();
    private readonly InMemoryWorkspaceRepository _workspaceRepo = new();
    private readonly InMemoryAuditLogRepository _auditRepo = new();
    private readonly InMemoryLlmCredentialStore _policyStore = new();
    private readonly ToolRegistry _tools = new();
    private readonly RecordingClient _client = new();
    private readonly WorkspaceService _workspaces;
    private readonly AgentChatService _chat;
    private readonly Guid _accountId = Guid.NewGuid();

    public AgentClarificationEvalTests()
    {
        var audit = new AuditLogService(_auditRepo, new InMemoryAccountRepository());
        _workspaces = new WorkspaceService(_workspaceRepo, new InMemoryAccountGroupRepository(), audit);

        _tools.Register(new StatePlanTool());
        _tools.Register(new RecordingTool("generate_data"));

        var credential = new ResolvedLlmCredential(
            LlmProvider.Anthropic, LlmCredentialKind.ApiKey, "claude-opus-5", "sk-test", null,
            new Dictionary<string, string>(), LlmCredentialSource.WorkspaceDefault);

        _chat = new AgentChatService(
            _sessions, _tools, _workspaces,
            new InstructionVersionService(new InMemoryInstructionVersionRepository(), _workspaces),
            new FixedResolver(credential), new FixedFactory(_client), new HeuristicTokenBudgetEstimator(),
            _policyStore, audit, _workspaceRepo, new PromptDataBoundary(), new UnlimitedUsage(), new LlmOptions());
    }

    private async Task<(Guid WorkspaceId, Guid UserId, ChatSession Session)> CreateSessionAsync(ChatMode mode)
    {
        var userId = Guid.NewGuid();
        var workspace = await _workspaces.CreateAsync(new CreateWorkspaceCommand(_accountId, "WS", userId));
        var session = await _chat.CreateSessionAsync(new CreateChatSessionCommand(workspace.Id, null, userId, "S", mode));
        return (workspace.Id, userId, session);
    }

    // ── the prompt contract ──────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task TheGuidanceForTheSessionsModeReachesTheProvider(Fixture fixture)
    {
        var (_, userId, session) = await CreateSessionAsync(fixture.Mode);
        _client.Enqueue(Text("acknowledged"));

        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, fixture.Prompt));

        var systemPrompt = _client.Requests.Should().ContainSingle().Subject.SystemInstructions!;

        systemPrompt.Should().Contain(fixture.MustMention,
            $"fixture '{fixture.Name}' exists because a {fixture.Mode} session needs that guidance");

        if (fixture.ExpectAskGuidance)
        {
            systemPrompt.ToLowerInvariant().Should().Contain("ask",
                "a mode that should ask has to be told to");
        }
    }

    [Fact]
    public async Task GuidedModeCoversEveryAmbiguityTrigger()
    {
        var (_, userId, session) = await CreateSessionAsync(ChatMode.Guided);
        _client.Enqueue(Text("ok"));

        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "generate some test data"));
        var systemPrompt = _client.Requests[0].SystemInstructions!;

        foreach (var trigger in AgentPromptGuidance.AmbiguityTriggers)
        {
            systemPrompt.Should().Contain(trigger,
                "every trigger the guidance declares must actually reach the model");
        }
    }

    [Fact]
    public async Task AutonomousModeAsksForAssumptionsRatherThanQuestions()
    {
        var (_, userId, session) = await CreateSessionAsync(ChatMode.Autonomous);
        _client.Enqueue(Text("ok"));

        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "generate some test data"));
        var systemPrompt = _client.Requests[0].SystemInstructions!;

        systemPrompt.Should().Contain("choose a sensible default and proceed");
        systemPrompt.Should().Contain("state every assumption you made");
        systemPrompt.Should().NotContain("make no tool call",
            "autonomous mode must not inherit the guided prohibition, or it would stop working");
    }

    [Fact]
    public async Task ThePlanRuleIsStatedOnlyWhenThePlanToolIsAvailable()
    {
        var (_, userId, session) = await CreateSessionAsync(ChatMode.Guided);
        _client.Enqueue(Text("ok"));
        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "hello"));
        _client.Requests[0].SystemInstructions!.Should().Contain(AgentPromptGuidance.PlanToolName);

        // A registry without the tool must not tell the model to call something it cannot see.
        var bare = new ToolRegistry();
        var audit = new AuditLogService(new InMemoryAuditLogRepository(), new InMemoryAccountRepository());
        var workspaces = new WorkspaceService(new InMemoryWorkspaceRepository(), new InMemoryAccountGroupRepository(), audit);
        var client = new RecordingClient();
        var chat = new AgentChatService(
            new InMemorySessionRepository(), bare, workspaces,
            new InstructionVersionService(new InMemoryInstructionVersionRepository(), workspaces),
            new FixedResolver(new ResolvedLlmCredential(LlmProvider.Anthropic, LlmCredentialKind.ApiKey, "m", "k", null, new Dictionary<string, string>(), LlmCredentialSource.WorkspaceDefault)),
            new FixedFactory(client), new HeuristicTokenBudgetEstimator(), new InMemoryLlmCredentialStore(),
            audit, new InMemoryWorkspaceRepository(), new PromptDataBoundary(), new UnlimitedUsage(), new LlmOptions());

        var bareUser = Guid.NewGuid();
        var bareWorkspace = await workspaces.CreateAsync(new CreateWorkspaceCommand(_accountId, "bare", bareUser));
        var bareSession = await chat.CreateSessionAsync(new CreateChatSessionCommand(bareWorkspace.Id, null, bareUser, "S", ChatMode.Guided));
        client.Enqueue(Text("ok"));
        await chat.SendMessageAsync(new SendMessageCommand(bareSession.Id, bareUser, "hello"));

        client.Requests[0].SystemInstructions!.Should().NotContain(AgentPromptGuidance.PlanToolName);
    }

    // ── harness behaviour ────────────────────────────────────────────────────────

    [Fact]
    public async Task AQuestionRunsNoToolAndEndsTheTurn()
    {
        var (_, userId, session) = await CreateSessionAsync(ChatMode.Guided);
        _client.Enqueue(Text("Which tables, and how many rows each?"));

        var turn = await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "generate some test data"));

        turn.ToolInvocations.Should().BeEmpty("an ambiguous request must not generate anything");
        turn.AssistantMessage!.Content.Should().Contain("Which tables");
        (await _sessions.ListInvocationsAsync(session.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task AStatedPlanIsRecordedAndAudited()
    {
        var (_, userId, session) = await CreateSessionAsync(ChatMode.Guided);
        _client.Enqueue(ToolCall(AgentPromptGuidance.PlanToolName, """{"steps":["discover the schema","generate 500 users"]}"""));
        _client.Enqueue(Text("Plan noted."));

        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "set up test data for orders"));

        var invocation = (await _sessions.ListInvocationsAsync(session.Id)).Should().ContainSingle().Subject;
        invocation.ToolName.Should().Be(AgentPromptGuidance.PlanToolName);
        invocation.Status.Should().Be(ToolInvocationStatus.Succeeded);
        invocation.InputJson.Should().Contain("discover the schema", "the steps live on the invocation row");

        var audited = _auditRepo.All.Should().ContainSingle(e => e.Action == "chat.plan_stated").Subject;
        audited.TargetId.Should().Be(invocation.Id.ToString());
        audited.Details.Should().NotContain("discover the schema",
            "plan steps are a tool payload and stay out of the audit log like every other one (#72)");
    }

    [Fact]
    public async Task ARevisedPlanIsAuditedAsARevision()
    {
        var (_, userId, session) = await CreateSessionAsync(ChatMode.Guided);
        _client.Enqueue(ToolCall(AgentPromptGuidance.PlanToolName, """{"steps":["generate 500 users"]}"""));
        _client.Enqueue(Text("Plan noted."));
        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "set up test data"));

        _client.Enqueue(ToolCall(AgentPromptGuidance.PlanToolName,
            """{"steps":["generate 500 users","generate 2000 orders"],"revises":"orders are needed too"}"""));
        _client.Enqueue(Text("Plan revised."));
        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "actually include orders"));

        var actions = _auditRepo.All.Where(e => e.Action.StartsWith("chat.plan", StringComparison.Ordinal))
            .Select(e => e.Action).ToArray();

        actions.Should().Equal(["chat.plan_stated", "chat.plan_revised"],
            "a revision has to be distinguishable from a first plan without reading the payload");
    }

    [Fact]
    public async Task AnEmptyPlanIsRejected()
    {
        var (_, userId, session) = await CreateSessionAsync(ChatMode.Guided);
        _client.Enqueue(ToolCall(AgentPromptGuidance.PlanToolName, """{"steps":[]}"""));
        _client.Enqueue(Text("done"));

        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "do something"));

        var invocation = (await _sessions.ListInvocationsAsync(session.Id)).Should().ContainSingle().Subject;
        invocation.Status.Should().Be(ToolInvocationStatus.Failed, "a plan with no steps is not a plan");
        _auditRepo.All.Should().NotContain(e => e.Action == "chat.plan_stated");
    }

    [Fact]
    public async Task AnUnambiguousRequestProceedsToTheTool()
    {
        var (_, userId, session) = await CreateSessionAsync(ChatMode.Guided);
        _client.Enqueue(ToolCall("generate_data", """{"rowCounts":{"main.users":500}}"""));
        _client.Enqueue(Text("Generated 500 rows."));

        var turn = await _chat.SendMessageAsync(
            new SendMessageCommand(session.Id, userId, "generate 500 rows in main.users using seed 4242"));

        turn.ToolInvocations.Should().ContainSingle().Which.ToolName.Should().Be("generate_data");
    }

    // ── stubs ────────────────────────────────────────────────────────────────────

    private static ChatCompletionResult Text(string text)
        => new(text, [], LlmStopReason.EndTurn, new TokenUsage(10, 5), "scripted");

    private static ChatCompletionResult ToolCall(string name, string argumentsJson)
        => new(string.Empty, [new LlmToolCall(Guid.NewGuid().ToString("N"), name, argumentsJson)],
            LlmStopReason.ToolUse, new TokenUsage(10, 5), "scripted");

    private sealed class RecordingClient : IChatCompletionClient
    {
        private readonly Queue<ChatCompletionResult> _responses = new();

        public List<ChatCompletionRequest> Requests { get; } = [];

        public string ProviderId => "scripted";

        public ModelCapabilities Capabilities => new(
            SupportsSampling: true, SupportsStreaming: false, SupportsToolCalling: true,
            SupportsEffort: false, SupportsStructuredOutput: false, MaxOutputTokens: 4096);

        public void Enqueue(ChatCompletionResult result) => _responses.Enqueue(result);

        public Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Count > 0 ? _responses.Dequeue() : Text("done"));
        }

        public async IAsyncEnumerable<ChatCompletionChunk> StreamAsync(ChatCompletionRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var result = await CompleteAsync(request, cancellationToken);
            yield return new ChatCompletionChunk(result.Text, result);
        }
    }

    private sealed class RecordingTool(string name) : ITool
    {
        public string Name => name;
        public string Description => name;

        public Task<string> ExecuteAsync(string inputJson, Guid sessionId, Guid userId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"ok":true}""");
    }

    private sealed class FixedResolver(ResolvedLlmCredential? credential) : ILlmCredentialResolver
    {
        public Task<ResolvedLlmCredential?> ResolveAsync(Guid workspaceId, Guid? projectId, LlmProvider? preferredProvider = null, CancellationToken ct = default)
            => Task.FromResult(credential);

        public Task<ResolvedLlmCredential?> ResolveAsync(Guid workspaceId, Guid? projectId, Guid? userId, Guid? sessionId, LlmProvider? preferredProvider = null, CancellationToken ct = default)
            => Task.FromResult(credential);
    }

    private sealed class FixedFactory(IChatCompletionClient client) : IChatCompletionClientFactory
    {
        public IChatCompletionClient Create(ResolvedLlmCredential credential, LlmCallContext? context = null) => client;
    }

    private sealed class UnlimitedUsage : ILlmUsageService
    {
        public Task<LlmUsageSummary> GetWorkspaceUsageAsync(Guid workspaceId, Guid requestingUserId, DateTimeOffset? from = null, DateTimeOffset? to = null, LlmUsageGrouping groupBy = LlmUsageGrouping.Model, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LlmUsageSummary> GetAccountUsageAsync(Guid accountId, Guid requestingUserId, DateTimeOffset? from = null, DateTimeOffset? to = null, LlmUsageGrouping groupBy = LlmUsageGrouping.Model, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<LlmBudgetVerdict> CheckBudgetAsync(Guid workspaceId, CancellationToken cancellationToken = default)
            => Task.FromResult(LlmBudgetVerdict.Allowed);
    }
}
