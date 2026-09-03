using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Fabricate.Tests.Infrastructure;

public sealed class EfLlmCredentialStoreTests : IDisposable
{
    private readonly FabricateDbContext _db;
    private readonly EfLlmCredentialStore _store;

    public EfLlmCredentialStoreTests()
    {
        var options = new DbContextOptionsBuilder<FabricateDbContext>().UseSqlite("Data Source=:memory:").Options;
        _db = new FabricateDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
        _store = new EfLlmCredentialStore(_db);
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    private static LlmCredential Credential(Guid workspaceId, string name, DateTimeOffset? revokedAt = null) => new(
        Guid.NewGuid(), workspaceId, null, name, LlmProvider.Anthropic, LlmCredentialKind.ApiKey,
        "CIPHERTEXT", "dp-v1", "fp", "1234", null, "claude-opus-5",
        new Dictionary<string, string> { ["region"] = "eu-west-1" }, false,
        revokedAt is null ? LlmCredentialStatus.Active : LlmCredentialStatus.Revoked,
        DateTimeOffset.UtcNow, Guid.NewGuid(), RevokedAt: revokedAt);

    [Fact]
    public async Task SaveAndList_RoundTripsSettingsAndOrdersByCreation()
    {
        var ws = Guid.NewGuid();
        await _store.SaveAsync(Credential(ws, "a"));
        await _store.SaveAsync(Credential(ws, "b"));
        await _store.SaveAsync(Credential(Guid.NewGuid(), "other"));

        var listed = await _store.ListByWorkspaceAsync(ws);

        listed.Select(c => c.Name).Should().Equal("a", "b");
        listed[0].NonSecretSettings["region"].Should().Be("eu-west-1");
    }

    [Fact]
    public async Task Save_UpdatesExistingRow()
    {
        var ws = Guid.NewGuid();
        var created = await _store.SaveAsync(Credential(ws, "a"));

        await _store.SaveAsync(created with { Status = LlmCredentialStatus.Revoked, RevokedAt = DateTimeOffset.UtcNow });

        (await _store.GetByIdAsync(created.Id))!.Status.Should().Be(LlmCredentialStatus.Revoked);
        (await _store.ListByWorkspaceAsync(ws)).Should().HaveCount(1);
    }

    [Fact]
    public async Task UniqueName_IsEnforcedAmongLiveCredentials_ButRevokedNamesCanBeReused()
    {
        var ws = Guid.NewGuid();
        await _store.SaveAsync(Credential(ws, "primary"));

        var duplicate = () => _store.SaveAsync(Credential(ws, "primary"));
        await duplicate.Should().ThrowAsync<DbUpdateException>();
        _db.ChangeTracker.Clear();

        await _store.SaveAsync(Credential(ws, "rotated-out", DateTimeOffset.UtcNow));
        await _store.SaveAsync(Credential(ws, "rotated-out"));

        (await _store.ListByWorkspaceAsync(ws)).Count(c => c.Name == "rotated-out").Should().Be(2);
    }

    [Fact]
    public async Task Policy_DefaultsToNull_AndUpserts()
    {
        var ws = Guid.NewGuid();
        (await _store.GetPolicyAsync(ws)).Should().BeNull();

        await _store.SavePolicyAsync(new WorkspaceLlmPolicy(ws, true, DateTimeOffset.UtcNow));
        await _store.SavePolicyAsync(new WorkspaceLlmPolicy(ws, false, DateTimeOffset.UtcNow));

        (await _store.GetPolicyAsync(ws))!.AllowPlatformFallback.Should().BeFalse();
    }
}
