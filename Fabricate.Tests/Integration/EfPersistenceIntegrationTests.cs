using Fabricate.Application.Abstractions;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Fabricate.Tests.Integration;

public sealed class EfPersistenceIntegrationTests : IDisposable
{
    private readonly FabricateDbContext _db;

    public EfPersistenceIntegrationTests()
    {
        var options = new DbContextOptionsBuilder<FabricateDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        _db = new FabricateDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    [Fact]
    public async Task EfAccountRepository_CanSaveAndRetrieve()
    {
        var repo = new EfAccountRepository(_db);
        var account = new Account(Guid.NewGuid(), "Acme Corp", DateTimeOffset.UtcNow);

        await repo.SaveAsync(account);
        var loaded = await repo.GetByIdAsync(account.Id);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Acme Corp");
    }

    [Fact]
    public async Task EfUserRepository_CanSaveAndQueryByEmail()
    {
        var repo = new EfUserRepository(_db);
        var user = new UserProfile(Guid.NewGuid(), "Alice", "alice@example.com", DateTimeOffset.UtcNow);

        await repo.SaveAsync(user);
        var found = await repo.GetByEmailAsync("alice@example.com");

        found.Should().NotBeNull();
        found!.DisplayName.Should().Be("Alice");
    }

    [Fact]
    public async Task EfAuditLogRepository_CanAppendAndQuery()
    {
        var repo = new EfAuditLogRepository(_db);
        var accountId = Guid.NewGuid();
        var evt = new AuditEvent(Guid.NewGuid(), accountId, null, "user.created", "User", "u1", "corr-1", DateTimeOffset.UtcNow);

        await repo.AppendAsync(evt);
        var page = await repo.QueryAsync(accountId, AuditFilter.None, 0, 10);
        var count = await repo.CountAsync(accountId, AuditFilter.None);

        page.Should().ContainSingle();
        count.Should().Be(1);
    }

    [Fact]
    public async Task EfRunRepository_CanCreateAndUpdate()
    {
        var repo = new EfRunRepository(_db);
        var rowCounts = (IReadOnlyDictionary<string, int>)new Dictionary<string, int> { ["t"] = 5 };
        var run = new DatasetRun(Guid.NewGuid(), RunStatus.Queued, DateTimeOffset.UtcNow, null, null, 42L, null, null, rowCounts);

        await repo.CreateAsync(run);
        var updated = run with { Status = RunStatus.Completed, CompletedAt = DateTimeOffset.UtcNow };
        await repo.UpdateAsync(updated);

        var loaded = await repo.GetByIdAsync(run.Id);
        loaded!.Status.Should().Be(RunStatus.Completed);
    }

    [Fact]
    public async Task EfApiKeyStore_CanSaveAndFindByHash()
    {
        var repo = new EfApiKeyStore(_db);
        var accountId = Guid.NewGuid();
        var key = new ApiKey(Guid.NewGuid(), accountId, "My Key", "hashed-abc123",
            new[] { "read" }, DateTimeOffset.UtcNow, null);

        await repo.SaveAsync(key);
        var found = await repo.FindByHashAsync("hashed-abc123");

        found.Should().NotBeNull();
        found!.Name.Should().Be("My Key");
    }

    [Fact]
    public async Task EfSessionRepository_CanSaveAndAddMessages()
    {
        var repo = new EfSessionRepository(_db);
        var session = new ChatSession(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), "Session 1", ChatMode.Guided, false, DateTimeOffset.UtcNow);

        await repo.SaveAsync(session);
        var msg = new ChatMessage(Guid.NewGuid(), session.Id, MessageRole.User, "hello", DateTimeOffset.UtcNow);
        await repo.SaveMessageAsync(msg);

        var messages = await repo.GetMessagesAsync(session.Id, 0, 50);
        messages.Should().ContainSingle();
    }
}
