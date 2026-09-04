using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using FluentAssertions;

namespace Fabricate.Tests.Application;

/// <summary>
/// #67: workspace access granted to an account group must reach that group's members. Before this,
/// GetEffectiveRoleAsync filtered group principals out, so such a user had no role anywhere in the platform.
/// </summary>
public sealed class WorkspaceGroupAccessTests
{
    private readonly TestServices _services = new();
    private readonly Guid _accountId = Guid.NewGuid();
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Guid _memberId = Guid.NewGuid();

    private async Task<Workspace> CreateWorkspaceAsync()
        => await _services.WorkspaceService.CreateAsync(new CreateWorkspaceCommand(_accountId, "WS", _adminId));

    private async Task<Guid> CreateGroupWithMemberAsync(Guid userId)
    {
        var groupId = Guid.NewGuid();
        await _services.AccountGroupRepository.SaveAsync(new AccountGroup(groupId, _accountId, "Engineers", DateTimeOffset.UtcNow));
        await _services.AccountGroupRepository.AddMemberAsync(new GroupMembership(groupId, userId, DateTimeOffset.UtcNow));
        return groupId;
    }

    [Fact]
    public async Task GroupGrant_GivesItsMembersTheGroupRole()
    {
        var workspace = await CreateWorkspaceAsync();
        var groupId = await CreateGroupWithMemberAsync(_memberId);

        (await _services.WorkspaceService.GetEffectiveRoleAsync(workspace.Id, _memberId))
            .Should().BeNull("no access has been granted yet");

        await _services.WorkspaceService.GrantAccessAsync(
            new GrantWorkspaceAccessCommand(workspace.Id, groupId, true, WorkspaceRole.Editor, _adminId));

        (await _services.WorkspaceService.GetEffectiveRoleAsync(workspace.Id, _memberId)).Should().Be(WorkspaceRole.Editor);
    }

    [Theory]
    [InlineData(WorkspaceRole.Admin, WorkspaceRole.Viewer, WorkspaceRole.Admin)]
    [InlineData(WorkspaceRole.Viewer, WorkspaceRole.Admin, WorkspaceRole.Admin)]
    [InlineData(WorkspaceRole.Editor, WorkspaceRole.Viewer, WorkspaceRole.Editor)]
    public async Task DirectAndGroupRoles_ResolveToTheHighest(WorkspaceRole direct, WorkspaceRole viaGroup, WorkspaceRole expected)
    {
        var workspace = await CreateWorkspaceAsync();
        var groupId = await CreateGroupWithMemberAsync(_memberId);

        await _services.WorkspaceService.GrantAccessAsync(new GrantWorkspaceAccessCommand(workspace.Id, _memberId, false, direct, _adminId));
        await _services.WorkspaceService.GrantAccessAsync(new GrantWorkspaceAccessCommand(workspace.Id, groupId, true, viaGroup, _adminId));

        (await _services.WorkspaceService.GetEffectiveRoleAsync(workspace.Id, _memberId)).Should().Be(expected);
    }

    [Fact]
    public async Task RemovingTheUserFromTheGroup_DropsTheInheritedRole()
    {
        var workspace = await CreateWorkspaceAsync();
        var groupId = await CreateGroupWithMemberAsync(_memberId);
        await _services.WorkspaceService.GrantAccessAsync(new GrantWorkspaceAccessCommand(workspace.Id, groupId, true, WorkspaceRole.Editor, _adminId));

        await _services.AccountGroupRepository.RemoveMemberAsync(groupId, _memberId);

        (await _services.WorkspaceService.GetEffectiveRoleAsync(workspace.Id, _memberId)).Should().BeNull();
    }

    [Fact]
    public async Task RevokingTheGroupsAccess_DropsTheInheritedRole()
    {
        var workspace = await CreateWorkspaceAsync();
        var groupId = await CreateGroupWithMemberAsync(_memberId);
        await _services.WorkspaceService.GrantAccessAsync(new GrantWorkspaceAccessCommand(workspace.Id, groupId, true, WorkspaceRole.Editor, _adminId));

        await _services.WorkspaceService.RevokeAccessAsync(workspace.Id, groupId, true, _adminId);

        (await _services.WorkspaceService.GetEffectiveRoleAsync(workspace.Id, _memberId)).Should().BeNull();
    }

    [Fact]
    public async Task AGroupTheUserDoesNotBelongTo_GrantsNothing()
    {
        var workspace = await CreateWorkspaceAsync();
        var otherGroupId = await CreateGroupWithMemberAsync(Guid.NewGuid());

        await _services.WorkspaceService.GrantAccessAsync(new GrantWorkspaceAccessCommand(workspace.Id, otherGroupId, true, WorkspaceRole.Admin, _adminId));

        (await _services.WorkspaceService.GetEffectiveRoleAsync(workspace.Id, _memberId)).Should().BeNull();
    }

    [Fact]
    public async Task GroupInheritedAdmin_CanManageWorkspaceAccess()
    {
        var workspace = await CreateWorkspaceAsync();
        var groupId = await CreateGroupWithMemberAsync(_memberId);
        await _services.WorkspaceService.GrantAccessAsync(new GrantWorkspaceAccessCommand(workspace.Id, groupId, true, WorkspaceRole.Admin, _adminId));

        // The inherited Admin is enough to pass the RequireAdmin check — the role is real, not display-only.
        var act = () => _services.WorkspaceService.GrantAccessAsync(
            new GrantWorkspaceAccessCommand(workspace.Id, Guid.NewGuid(), false, WorkspaceRole.Viewer, _memberId));

        await act.Should().NotThrowAsync();
    }
}
