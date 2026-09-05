using Fabricate.Application.Abstractions;
using Fabricate.Application.Governance;
using Fabricate.Application.Llm;
using Fabricate.Application.Workspaces;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;

namespace Fabricate.Tests.Application;

/// <summary>
/// #77: every turn recorded its token usage and every call was logged, but nothing aggregated either — so "what
/// is this workspace spending" and "has it spent too much" had no answer. These cover the rollups and the budget.
/// </summary>
public sealed class LlmUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryLlmUsageRepository _usage = new();
    private readonly InMemoryWorkspaceRepository _workspaceRepo = new();
    private readonly InMemoryAccountRepository _accounts = new();
    private readonly InMemoryLlmCredentialStore _policyStore = new();
    private readonly WorkspaceService _workspaces;
    private readonly LlmUsageService _service;

    private readonly Guid _accountId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();

    public LlmUsageTests()
    {
        var audit = new AuditLogService(new InMemoryAuditLogRepository(), _accounts);
        _workspaces = new WorkspaceService(_workspaceRepo, new InMemoryAccountGroupRepository(), audit);
        _service = new LlmUsageService(_usage, _workspaces, _workspaceRepo, _accounts, _policyStore, new FixedTime(Now));
    }

    private async Task<Workspace> CreateWorkspaceAsync(string name = "WS")
    {
        await _accounts.SaveAsync(new Account(_accountId, "Acme", Now));
        await _accounts.AddMemberAsync(new AccountMembership(_accountId, _ownerId, AccountRole.Owner, Now));
        return await _workspaces.CreateAsync(new CreateWorkspaceCommand(_accountId, name, _ownerId));
    }

    private Task RecordAsync(
        Guid workspaceId,
        string model = "claude-opus-5",
        long input = 100,
        long output = 50,
        Guid? credentialId = null,
        DateTimeOffset? at = null,
        LlmCallOutcome outcome = LlmCallOutcome.Success,
        int attempt = 1)
        => _usage.RecordAsync(new LlmUsageRecord(
            Guid.NewGuid(), workspaceId, null, null, credentialId, "anthropic", model,
            input, output, attempt, 120, outcome, at ?? Now.AddMinutes(-5)));

    // ── rollups ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UsageSumsPerModel()
    {
        var ws = await CreateWorkspaceAsync();
        await RecordAsync(ws.Id, "claude-opus-5", 100, 50);
        await RecordAsync(ws.Id, "claude-opus-5", 200, 25);
        await RecordAsync(ws.Id, "gpt-x", 10, 5);

        var summary = await _service.GetWorkspaceUsageAsync(ws.Id, _ownerId, groupBy: LlmUsageGrouping.Model);

        summary.TotalTokens.Should().Be(390);
        summary.Calls.Should().Be(3);
        summary.Buckets.Should().HaveCount(2);

        var opus = summary.Buckets.Single(b => b.Key == "claude-opus-5");
        opus.InputTokens.Should().Be(300);
        opus.OutputTokens.Should().Be(75);
        opus.Calls.Should().Be(2);

        summary.Buckets[0].Key.Should().Be("claude-opus-5", "buckets are ordered by spend, heaviest first");
    }

    [Fact]
    public async Task UsageSumsPerCredential_AndPlatformCallsAreTheirOwnBucket()
    {
        var ws = await CreateWorkspaceAsync();
        var credential = Guid.NewGuid();
        await RecordAsync(ws.Id, credentialId: credential, input: 100, output: 100);
        await RecordAsync(ws.Id, credentialId: credential, input: 50, output: 50);
        await RecordAsync(ws.Id, credentialId: null, input: 10, output: 10);

        var summary = await _service.GetWorkspaceUsageAsync(ws.Id, _ownerId, groupBy: LlmUsageGrouping.Credential);

        summary.Buckets.Single(b => b.Key == credential.ToString()).TotalTokens.Should().Be(300);
        summary.Buckets.Single(b => b.Key == "platform").TotalTokens.Should().Be(20,
            "a call on the operator's own credential has no credential id, and lumping it in with a tenant's would misattribute it");
    }

    [Fact]
    public async Task UsageGroupsByUtcDay()
    {
        var ws = await CreateWorkspaceAsync();
        await RecordAsync(ws.Id, at: Now.AddDays(-1), input: 10, output: 0);
        await RecordAsync(ws.Id, at: Now.AddHours(-1), input: 20, output: 0);
        await RecordAsync(ws.Id, at: Now.AddHours(-2), input: 30, output: 0);

        var summary = await _service.GetWorkspaceUsageAsync(ws.Id, _ownerId, groupBy: LlmUsageGrouping.Day);

        summary.Buckets.Should().HaveCount(2);
        summary.Buckets.Single(b => b.Key == "2026-09-05").TotalTokens.Should().Be(50);
        summary.Buckets.Single(b => b.Key == "2026-09-04").TotalTokens.Should().Be(10);
    }

    [Fact]
    public async Task FailedCallsAreCountedSeparatelyFromSuccessfulOnes()
    {
        var ws = await CreateWorkspaceAsync();
        await RecordAsync(ws.Id);
        await RecordAsync(ws.Id, outcome: LlmCallOutcome.RetriedFailure, attempt: 1, input: 0, output: 0);
        await RecordAsync(ws.Id, outcome: LlmCallOutcome.Failure, attempt: 2, input: 0, output: 0);

        var summary = await _service.GetWorkspaceUsageAsync(ws.Id, _ownerId);

        summary.Calls.Should().Be(3);
        summary.FailedCalls.Should().Be(2, "a workspace whose calls keep failing is burning latency, and that must be visible");
    }

    [Fact]
    public async Task UsageIsScopedToTheWorkspaceAndTheWindow()
    {
        var mine = await CreateWorkspaceAsync("mine");
        var other = await _workspaces.CreateAsync(new CreateWorkspaceCommand(_accountId, "other", _ownerId));

        await RecordAsync(mine.Id, input: 100, output: 0);
        await RecordAsync(mine.Id, input: 999, output: 0, at: Now.AddDays(-90));
        await RecordAsync(other.Id, input: 500, output: 0);

        var summary = await _service.GetWorkspaceUsageAsync(mine.Id, _ownerId, from: Now.AddDays(-7), to: Now);

        summary.TotalTokens.Should().Be(100);
    }

    [Fact]
    public async Task TheAccountRollupSpansEveryWorkspace_AndIsOwnersOnly()
    {
        var first = await CreateWorkspaceAsync("first");
        var second = await _workspaces.CreateAsync(new CreateWorkspaceCommand(_accountId, "second", _ownerId));
        await RecordAsync(first.Id, input: 100, output: 0);
        await RecordAsync(second.Id, input: 200, output: 0);

        var summary = await _service.GetAccountUsageAsync(_accountId, _ownerId);
        summary.TotalTokens.Should().Be(300);

        var memberId = Guid.NewGuid();
        await _accounts.AddMemberAsync(new AccountMembership(_accountId, memberId, AccountRole.Member, Now));
        var asMember = async () => await _service.GetAccountUsageAsync(_accountId, memberId);
        await asMember.Should().ThrowAsync<UnauthorizedAccessException>(
            "the rollup spans workspaces the caller may not individually belong to");
    }

    [Fact]
    public async Task ReadingUsageRequiresWorkspaceAccess()
    {
        var ws = await CreateWorkspaceAsync();

        var asStranger = async () => await _service.GetWorkspaceUsageAsync(ws.Id, Guid.NewGuid());

        await asStranger.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    // ── budgets ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoBudgetMeansNoLimit()
    {
        var ws = await CreateWorkspaceAsync();
        await RecordAsync(ws.Id, input: 1_000_000, output: 1_000_000);

        (await _service.CheckBudgetAsync(ws.Id)).IsWithinBudget.Should().BeTrue();
    }

    [Fact]
    public async Task ADailyBudgetBlocksOnceItIsReached()
    {
        var ws = await CreateWorkspaceAsync();
        await _policyStore.SavePolicyAsync(new WorkspaceLlmPolicy(ws.Id, false, Now, null, false, DailyTokenBudget: 500));

        await RecordAsync(ws.Id, input: 300, output: 100);
        (await _service.CheckBudgetAsync(ws.Id)).IsWithinBudget.Should().BeTrue("400 of 500 leaves room");

        await RecordAsync(ws.Id, input: 100, output: 0);
        var verdict = await _service.CheckBudgetAsync(ws.Id);

        verdict.IsWithinBudget.Should().BeFalse("500 of 500 is spent");
        verdict.Reason.Should().Contain("500").And.Contain("daily");
        verdict.Reason.Should().Contain("00:00 UTC", "the notice has to say when it lifts");
    }

    [Fact]
    public async Task TheDailyBudgetIgnoresYesterdaysSpend()
    {
        var ws = await CreateWorkspaceAsync();
        await _policyStore.SavePolicyAsync(new WorkspaceLlmPolicy(ws.Id, false, Now, null, false, DailyTokenBudget: 500));

        await RecordAsync(ws.Id, input: 10_000, output: 0, at: Now.AddDays(-1));

        (await _service.CheckBudgetAsync(ws.Id)).IsWithinBudget.Should().BeTrue(
            "the daily budget resets at UTC midnight, so yesterday's spend cannot block today");
    }

    [Fact]
    public async Task AMonthlyBudgetCountsTheCalendarMonth()
    {
        var ws = await CreateWorkspaceAsync();
        await _policyStore.SavePolicyAsync(new WorkspaceLlmPolicy(ws.Id, false, Now, null, false, MonthlyTokenBudget: 1_000));

        // Same month, but earlier — counted. Last month — not.
        await RecordAsync(ws.Id, input: 600, output: 0, at: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        await RecordAsync(ws.Id, input: 10_000, output: 0, at: new DateTimeOffset(2026, 8, 31, 23, 0, 0, TimeSpan.Zero));

        (await _service.CheckBudgetAsync(ws.Id)).IsWithinBudget.Should().BeTrue();

        await RecordAsync(ws.Id, input: 400, output: 0);
        var verdict = await _service.CheckBudgetAsync(ws.Id);

        verdict.IsWithinBudget.Should().BeFalse();
        verdict.Reason.Should().Contain("monthly");
    }

    [Fact]
    public async Task TheDailyBudgetIsReportedBeforeTheMonthlyOne()
    {
        var ws = await CreateWorkspaceAsync();
        await _policyStore.SavePolicyAsync(
            new WorkspaceLlmPolicy(ws.Id, false, Now, null, false, DailyTokenBudget: 100, MonthlyTokenBudget: 100));

        await RecordAsync(ws.Id, input: 100, output: 0);

        (await _service.CheckBudgetAsync(ws.Id)).Reason.Should().Contain("daily",
            "the tighter window is the more actionable message");
    }

    /// <summary>A clock the budget windows can be measured against without waiting for midnight.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
