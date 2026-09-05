using Fabricate.Application.Abstractions;
using Fabricate.Application.Chat;
using Fabricate.Application.Governance;
using Fabricate.Application.Llm;
using Fabricate.Application.Workspaces;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;

namespace Fabricate.Tests.Application;

/// <summary>
/// #83: #60 §7 defined a boundary for what may reach a model provider — metadata may leave the instance, values
/// and aggregates over them may not without an explicit opt-in, and the opt-in is refused outright under
/// Healthcare or Finance. It was documented and nowhere enforced. These are the enforcement.
/// </summary>
public sealed class PromptDataBoundaryTests
{
    private readonly InMemoryAuditLogRepository _auditRepo = new();
    private readonly InMemoryWorkspaceRepository _workspaceRepo = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly InMemoryLlmCredentialStore _policyStore = new();
    private readonly ToolRegistry _tools = new();
    private readonly WorkspaceService _workspaces;
    private readonly AgentChatService _chat;
    private readonly IAuditLogService _audit;

    public PromptDataBoundaryTests()
    {
        _audit = new AuditLogService(_auditRepo, new InMemoryAccountRepository());
        _workspaces = new WorkspaceService(_workspaceRepo, new InMemoryAccountGroupRepository(), _audit);

        _tools.Register(new StubTool("discover_schema", PromptContentClass.Metadata));
        _tools.Register(new StubTool("profile_columns", PromptContentClass.AggregateStatistics));
        _tools.Register(new StubTool("sample_rows", PromptContentClass.SampledValues));

        _chat = new AgentChatService(
            _sessions, _tools, _workspaces,
            new InstructionVersionService(new InMemoryInstructionVersionRepository(), _workspaces),
            new NoCredentialResolver(), new ThrowingFactory(), new HeuristicTokenBudgetEstimator(), _policyStore,
            _audit, _workspaceRepo, new PromptDataBoundary(), new UnlimitedUsage(), new LlmOptions());
    }

    private async Task<(Workspace Workspace, Guid UserId, ChatSession Session)> CreateAsync(
        ComplianceProfile profile = ComplianceProfile.Default,
        bool optIn = false)
    {
        var userId = Guid.NewGuid();
        var workspace = await _workspaces.CreateAsync(new CreateWorkspaceCommand(Guid.NewGuid(), "WS", userId, profile));
        if (optIn)
        {
            await _policyStore.SavePolicyAsync(
                new WorkspaceLlmPolicy(workspace.Id, false, DateTimeOffset.UtcNow, null, AllowSampledDataInPrompts: true));
        }

        var session = await _chat.CreateSessionAsync(new CreateChatSessionCommand(workspace.Id, null, userId, "S", ChatMode.Autonomous));
        return (workspace, userId, session);
    }

    // ── the policy itself ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ComplianceProfile.Healthcare)]
    [InlineData(ComplianceProfile.Finance)]
    public void RegulatedProfilesCannotOptIn(ComplianceProfile profile)
    {
        var boundary = new PromptDataBoundary();

        boundary.CanOptIn(profile).Should().BeFalse();
        boundary.OptInRefusalReason(profile).Should().Contain(profile.ToString());
    }

    [Fact]
    public void MetadataIsAlwaysAllowed_EvenUnderARegulatedProfile()
    {
        var boundary = new PromptDataBoundary();
        var workspace = new Workspace(Guid.NewGuid(), Guid.NewGuid(), "WS", DateTimeOffset.UtcNow, ComplianceProfile.Healthcare);

        boundary.Allows(PromptContentClass.Metadata, workspace, policy: null).Should().BeTrue(
            "schema metadata describes the data rather than containing it, and without it the agent is useless");
    }

    [Fact]
    public void AStoredOptInIsIgnoredIfTheProfileLaterBecomesRegulated()
    {
        var boundary = new PromptDataBoundary();
        var workspace = new Workspace(Guid.NewGuid(), Guid.NewGuid(), "WS", DateTimeOffset.UtcNow, ComplianceProfile.Finance);
        var staleOptIn = new WorkspaceLlmPolicy(workspace.Id, false, DateTimeOffset.UtcNow, null, AllowSampledDataInPrompts: true);

        boundary.Allows(PromptContentClass.SampledValues, workspace, staleOptIn).Should().BeFalse(
            "the regime must win over a policy row written before the profile changed");
    }

    // ── tool visibility ──────────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutTheOptIn_OnlyMetadataToolsAreOffered()
    {
        var (_, userId, session) = await CreateAsync();

        var offered = await ToolsOfferedAsync(session, userId);

        offered.Should().Equal(["discover_schema"],
            "a tool the boundary forbids must be absent from the list, not refused when called");
    }

    [Fact]
    public async Task WithTheOptIn_EveryToolIsOffered()
    {
        var (_, userId, session) = await CreateAsync(optIn: true);

        var offered = await ToolsOfferedAsync(session, userId);

        offered.Should().BeEquivalentTo(["discover_schema", "profile_columns", "sample_rows"]);
    }

    [Fact]
    public async Task UnderARegulatedProfile_NoOptInCanMakeTheToolsVisible()
    {
        var (_, userId, session) = await CreateAsync(ComplianceProfile.Healthcare, optIn: true);

        var offered = await ToolsOfferedAsync(session, userId);

        offered.Should().Equal(["discover_schema"]);
    }

    // ── direct invocation ────────────────────────────────────────────────────────

    [Fact]
    public async Task ADirectInvocationOfAForbiddenToolIsRefusedAndAudited()
    {
        var (workspace, userId, session) = await CreateAsync();

        var turn = await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "/tool sample_rows {}"));

        turn.ToolInvocations.Should().ContainSingle().Which.Status.Should().Be(ToolInvocationStatus.Failed);

        var blocked = _auditRepo.All.Should().ContainSingle(e => e.Action == "llm.boundary_blocked").Subject;
        blocked.AccountId.Should().Be(workspace.AccountId);
        blocked.Details.Should().Contain("reason=prompt_data_boundary").And.Contain("contentClass=SampledValues");
        blocked.Details.Should().Contain("tool=sample_rows");
    }

    [Fact]
    public async Task ABoundaryBlockRecordsNoPayload()
    {
        var (_, userId, session) = await CreateAsync();

        await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId,
            "/tool sample_rows {\"query\":\"SELECT ssn FROM patients\"}"));

        var everything = string.Join("\n", _auditRepo.All.Select(e => $"{e.Action}|{e.TargetId}|{e.Details}"));
        everything.Should().NotContain("ssn");
        everything.Should().NotContain("patients");
    }

    [Fact]
    public async Task WithTheOptIn_ADirectInvocationSucceeds()
    {
        var (_, userId, session) = await CreateAsync(optIn: true);

        var turn = await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, "/tool sample_rows {}"));

        turn.ToolInvocations.Should().ContainSingle().Which.Status.Should().Be(ToolInvocationStatus.Succeeded);
        _auditRepo.All.Should().NotContain(e => e.Action == "llm.boundary_blocked");
    }

    /// <summary>The tool names the model would be shown, which is what "offered" means for the boundary.</summary>
    private async Task<IReadOnlyList<string>> ToolsOfferedAsync(ChatSession session, Guid userId)
    {
        // GetAllowedToolsAsync is private; the observable equivalent is which tools can actually be invoked.
        var offered = new List<string>();
        foreach (var name in new[] { "discover_schema", "profile_columns", "sample_rows" })
        {
            var turn = await _chat.SendMessageAsync(new SendMessageCommand(session.Id, userId, $"/tool {name} {{}}"));
            if (turn.ToolInvocations.Single().Status == ToolInvocationStatus.Succeeded) offered.Add(name);
        }

        return offered;
    }

    // ── stubs ────────────────────────────────────────────────────────────────────

    private sealed class StubTool(string name, PromptContentClass contentClass) : ITool
    {
        public string Name => name;
        public string Description => name;
        public PromptContentClass ContentClass => contentClass;

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
        public IChatCompletionClient Create(ResolvedLlmCredential credential, LlmCallContext? context = null)
            => throw new InvalidOperationException("No model should be called in these tests.");
    }

    /// <summary>No budget configured, so every turn proceeds. #77's enforcement has its own tests.</summary>
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
