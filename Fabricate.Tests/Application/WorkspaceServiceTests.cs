using Fabricate.Application.Abstractions;
using Fabricate.Application.Governance;
using Fabricate.Application.Workspaces;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class WorkspaceServiceTests
{
    private readonly InMemoryAccountRepository _accountRepo = new();
    private readonly InMemoryAuditLogRepository _auditLogRepo = new();
    private readonly IAuditLogService _auditLogService;
    private readonly WorkspaceService _service;

    public WorkspaceServiceTests()
    {
        _auditLogService = new AuditLogService(_auditLogRepo);
        _service = new WorkspaceService(new InMemoryWorkspaceRepository(), new InMemoryAccountGroupRepository(), _auditLogService);
    }

    private async Task<(Account account, Guid ownerId)> CreateAccountAsync()
    {
        var ownerId = Guid.NewGuid();
        var account = new Account(Guid.NewGuid(), "Org", DateTimeOffset.UtcNow);
        await _accountRepo.SaveAsync(account);
        await _accountRepo.AddMemberAsync(new AccountMembership(account.Id, ownerId, AccountRole.Owner, DateTimeOffset.UtcNow));
        return (account, ownerId);
    }

    [Fact]
    public async Task CreateAsync_ShouldCreateWorkspaceWithCreatorAsAdmin()
    {
        var (account, ownerId) = await CreateAccountAsync();

        var workspace = await _service.CreateAsync(new CreateWorkspaceCommand(account.Id, "Dev Workspace", ownerId));

        workspace.Name.Should().Be("Dev Workspace");
        workspace.AccountId.Should().Be(account.Id);

        var role = await _service.GetEffectiveRoleAsync(workspace.Id, ownerId);
        role.Should().Be(WorkspaceRole.Admin);
    }

    [Fact]
    public async Task GetEffectiveRoleAsync_ShouldReturnNullForNonMember()
    {
        var (account, ownerId) = await CreateAccountAsync();
        var workspace = await _service.CreateAsync(new CreateWorkspaceCommand(account.Id, "WS", ownerId));

        var role = await _service.GetEffectiveRoleAsync(workspace.Id, Guid.NewGuid());
        role.Should().BeNull();
    }

    [Fact]
    public async Task GrantAccessAsync_ShouldAddMembership()
    {
        var (account, ownerId) = await CreateAccountAsync();
        var workspace = await _service.CreateAsync(new CreateWorkspaceCommand(account.Id, "WS", ownerId));
        var editorId = Guid.NewGuid();

        await _service.GrantAccessAsync(new GrantWorkspaceAccessCommand(workspace.Id, editorId, false, WorkspaceRole.Editor, ownerId));

        var role = await _service.GetEffectiveRoleAsync(workspace.Id, editorId);
        role.Should().Be(WorkspaceRole.Editor);
    }

    [Fact]
    public async Task GrantAccessAsync_ShouldThrowIfRequestingUserNotAdmin()
    {
        var (account, ownerId) = await CreateAccountAsync();
        var workspace = await _service.CreateAsync(new CreateWorkspaceCommand(account.Id, "WS", ownerId));
        var editorId = Guid.NewGuid();
        await _service.GrantAccessAsync(new GrantWorkspaceAccessCommand(workspace.Id, editorId, false, WorkspaceRole.Editor, ownerId));

        var newUser = Guid.NewGuid();
        var act = async () => await _service.GrantAccessAsync(new GrantWorkspaceAccessCommand(workspace.Id, newUser, false, WorkspaceRole.Viewer, editorId));
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task RevokeAccessAsync_ShouldRemoveMembership()
    {
        var (account, ownerId) = await CreateAccountAsync();
        var workspace = await _service.CreateAsync(new CreateWorkspaceCommand(account.Id, "WS", ownerId));
        var viewerId = Guid.NewGuid();
        await _service.GrantAccessAsync(new GrantWorkspaceAccessCommand(workspace.Id, viewerId, false, WorkspaceRole.Viewer, ownerId));

        await _service.RevokeAccessAsync(workspace.Id, viewerId, false, ownerId);

        var role = await _service.GetEffectiveRoleAsync(workspace.Id, viewerId);
        role.Should().BeNull();
    }
}
