using Fabricate.Application.Governance;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;

namespace Fabricate.Tests.Application;

/// <summary>
/// #74: audit rows were insert-only and grew without bound, and there was no way to get them out of the system.
/// These cover the retention window's boundaries, the batching, and the export's authorisation and redaction.
/// </summary>
public sealed class AuditRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryAuditLogRepository _repo = new();
    private readonly InMemoryAccountRepository _accounts = new();
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly Guid _ownerId = Guid.NewGuid();

    private AuditLogService Service(int retentionDays = 0, int batchSize = 1_000)
        => new(_repo, _accounts,
            new AuditRetentionOptions { RetentionDays = retentionDays, BatchSize = batchSize },
            new FixedTime(Now));

    private async Task SeedOwnerAsync()
    {
        await _accounts.SaveAsync(new Account(_accountId, "Acme", Now));
        await _accounts.AddMemberAsync(new AccountMembership(_accountId, _ownerId, AccountRole.Owner, Now));
    }

    private Task RecordAsync(Guid accountId, int daysAgo, string action = "test.event", string? details = null)
        => _repo.AppendAsync(new AuditEvent(
            Guid.NewGuid(), accountId, null, action, "Thing", "1", "corr", Now.AddDays(-daysAgo), details));

    // ── retention ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RetentionPurgesEventsBeyondTheWindow_AndKeepsEventsInsideIt()
    {
        await RecordAsync(_accountId, 31, "old.event");
        await RecordAsync(_accountId, 29, "recent.event");

        var deleted = await Service(retentionDays: 30).ApplyRetentionAsync();

        deleted.Should().Be(1);
        _repo.All.Select(e => e.Action).Should().Contain("recent.event");
        _repo.All.Select(e => e.Action).Should().NotContain("old.event");
    }

    [Fact]
    public async Task AnEventExactlyOnTheBoundaryIsKept()
    {
        await RecordAsync(_accountId, 30, "boundary.event");

        var deleted = await Service(retentionDays: 30).ApplyRetentionAsync();

        deleted.Should().Be(0, "retention deletes what is older than the window, not what sits exactly on its edge");
        _repo.All.Should().ContainSingle();
    }

    [Fact]
    public async Task RetentionDisabledByDefault_KeepsEverything()
    {
        await RecordAsync(_accountId, 3_650, "ancient.event");

        var deleted = await Service(retentionDays: 0).ApplyRetentionAsync();

        deleted.Should().Be(0, "the default must not silently start deleting an existing deployment's audit log");
        _repo.All.Should().ContainSingle();
    }

    [Fact]
    public async Task RetentionPurgesAcrossEveryAccount_AndAuditsItself()
    {
        var otherAccount = Guid.NewGuid();
        await RecordAsync(_accountId, 40);
        await RecordAsync(otherAccount, 40);
        await RecordAsync(otherAccount, 1);

        var deleted = await Service(retentionDays: 30).ApplyRetentionAsync();

        deleted.Should().Be(2, "retention is a system-wide sweep, not a per-account one");

        var purge = _repo.All.Should().ContainSingle(e => e.Action == "audit.retention_applied").Subject;
        purge.Details.Should().Contain("deleted=2").And.Contain("retentionDays=30");
        purge.AccountId.Should().Be(Guid.Empty, "the sweep belongs to no single account");
    }

    [Fact]
    public async Task ANoOpSweepRecordsNothing()
    {
        await RecordAsync(_accountId, 1);

        await Service(retentionDays: 30).ApplyRetentionAsync();

        _repo.All.Should().NotContain(e => e.Action == "audit.retention_applied",
            "a sweep that deleted nothing would otherwise add a row on every tick — unbounded growth of its own");
    }

    [Fact]
    public async Task ABacklogLargerThanOneBatchIsFullyPurged()
    {
        for (var i = 0; i < 25; i++) await RecordAsync(_accountId, 40);

        var deleted = await Service(retentionDays: 30, batchSize: 10).ApplyRetentionAsync();

        deleted.Should().Be(25, "the sweep must keep batching until the window is clear");
        _repo.All.Should().ContainSingle(e => e.Action == "audit.retention_applied");
    }

    // ── export ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportIsRefusedToNonOwners()
    {
        await SeedOwnerAsync();
        var memberId = Guid.NewGuid();
        await _accounts.AddMemberAsync(new AccountMembership(_accountId, memberId, AccountRole.Member, Now));
        await RecordAsync(_accountId, 1);

        var asMember = async () => await Collect(Service().ExportAsync(_accountId, memberId));
        var asStranger = async () => await Collect(Service().ExportAsync(_accountId, Guid.NewGuid()));

        await asMember.Should().ThrowAsync<UnauthorizedAccessException>();
        await asStranger.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ExportReturnsOnlyTheAccountsOwnEvents_InTheRequestedWindow()
    {
        await SeedOwnerAsync();
        await RecordAsync(_accountId, 10, "in.window.old");
        await RecordAsync(_accountId, 5, "in.window.recent");
        await RecordAsync(_accountId, 100, "out.of.window");
        await RecordAsync(Guid.NewGuid(), 5, "other.account");

        var exported = await Collect(Service().ExportAsync(_accountId, _ownerId, from: Now.AddDays(-30), to: Now));

        exported.Select(e => e.Action).Should().Equal(["in.window.old", "in.window.recent"],
            "export is scoped to one account, filtered to the window, and ordered oldest first");
    }

    [Fact]
    public async Task ExportRedactsSensitiveDetails()
    {
        await SeedOwnerAsync();
        await RecordAsync(_accountId, 1, "llm.credential_registered",
            "provider=Anthropic;model=claude-opus-5;fingerprint=9f3c2a1b;secret=sk-ant-api03-LIVE-KEY-VALUE");
        await RecordAsync(_accountId, 1, "connection.created",
            "target=Host=db.internal;Username=app;Password=hunter2;Database=prod");

        var exported = await Collect(Service().ExportAsync(_accountId, _ownerId));
        var details = string.Join("\n", exported.Select(e => e.Details));

        details.Should().NotContain("sk-ant-api03-LIVE-KEY-VALUE");
        details.Should().NotContain("9f3c2a1b", "a credential fingerprint identifies a live key across tenants");
        details.Should().NotContain("hunter2");
        details.Should().Contain("provider=Anthropic", "redaction must not gut the fields that make the log useful");
        details.Should().Contain("model=claude-opus-5");
    }

    [Fact]
    public async Task ExportLeavesTheStoredEventUntouched()
    {
        await SeedOwnerAsync();
        await RecordAsync(_accountId, 1, "llm.credential_registered", "secret=sk-ant-api03-LIVE-KEY-VALUE");

        await Collect(Service().ExportAsync(_accountId, _ownerId));

        _repo.All.Single(e => e.Action == "llm.credential_registered").Details
            .Should().Contain("sk-ant-api03-LIVE-KEY-VALUE",
                "redaction belongs to the export, not to the store — rewriting history in place would be worse");
    }

    [Fact]
    public async Task ExportReturnsTheSameEventsAsTheQueryApi()
    {
        await SeedOwnerAsync();
        for (var i = 1; i <= 5; i++) await RecordAsync(_accountId, i, $"event.{i}");

        var service = Service();
        var queried = await service.QueryAsync(_accountId, page: 1, pageSize: 100);
        var exported = await Collect(service.ExportAsync(_accountId, _ownerId));

        exported.Select(e => e.Id).Should().BeEquivalentTo(queried.Events.Select(e => e.Id),
            "the export must not be a different view of the log from the one the query API shows");
    }

    private static async Task<List<AuditEvent>> Collect(IAsyncEnumerable<AuditEvent> events)
    {
        var collected = new List<AuditEvent>();
        await foreach (var auditEvent in events) collected.Add(auditEvent);
        return collected;
    }

    /// <summary>A clock the retention window can be measured against without waiting for real days to pass.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
