using Fabricate.Application.Abstractions;
using Fabricate.Application.Governance;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class AuditLogServiceTests
{
    private readonly InMemoryAuditLogRepository _repo = new();
    private readonly AuditLogService _service;

    public AuditLogServiceTests()
    {
        _service = new AuditLogService(_repo);
    }

    [Fact]
    public async Task RecordAsync_ShouldPersistEvent()
    {
        var accountId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await _service.RecordAsync(new AuditEvent(Guid.NewGuid(), accountId, userId, "user.created", "User", userId.ToString(), "req-1", DateTimeOffset.UtcNow));

        var page = await _service.QueryAsync(accountId);
        page.TotalCount.Should().Be(1);
        page.Events.Should().ContainSingle(e => e.Action == "user.created");
    }

    [Fact]
    public async Task QueryAsync_ShouldFilterByAction()
    {
        var accountId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _service.RecordAsync(new AuditEvent(Guid.NewGuid(), accountId, userId, "user.created", "User", userId.ToString(), "req-1", DateTimeOffset.UtcNow));
        await _service.RecordAsync(new AuditEvent(Guid.NewGuid(), accountId, userId, "workspace.created", "Workspace", Guid.NewGuid().ToString(), "req-2", DateTimeOffset.UtcNow));

        var page = await _service.QueryAsync(accountId, actionFilter: "workspace");
        page.TotalCount.Should().Be(1);
        page.Events.Should().AllSatisfy(e => e.Action.Should().Contain("workspace"));
    }

    [Fact]
    public async Task QueryAsync_ShouldBeScopedToAccount()
    {
        var accountId1 = Guid.NewGuid();
        var accountId2 = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await _service.RecordAsync(new AuditEvent(Guid.NewGuid(), accountId1, userId, "user.created", "User", userId.ToString(), "req-1", DateTimeOffset.UtcNow));
        await _service.RecordAsync(new AuditEvent(Guid.NewGuid(), accountId2, userId, "user.created", "User", userId.ToString(), "req-2", DateTimeOffset.UtcNow));

        var page = await _service.QueryAsync(accountId1);
        page.TotalCount.Should().Be(1);
        page.Events.Should().AllSatisfy(e => e.AccountId.Should().Be(accountId1));
    }

    [Fact]
    public async Task QueryAsync_ShouldPaginate()
    {
        var accountId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        for (var i = 0; i < 10; i++)
        {
            await _service.RecordAsync(new AuditEvent(Guid.NewGuid(), accountId, userId, $"event.{i}", "Entity", Guid.NewGuid().ToString(), $"req-{i}", DateTimeOffset.UtcNow));
        }

        var page = await _service.QueryAsync(accountId, page: 1, pageSize: 3);
        page.Events.Should().HaveCount(3);
        page.TotalCount.Should().Be(10);
    }
}
