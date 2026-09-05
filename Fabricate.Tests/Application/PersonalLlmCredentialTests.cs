using Fabricate.Application.Abstractions;
using Fabricate.Application.Governance;
using Fabricate.Application.Llm;
using Fabricate.Application.Workspaces;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;

namespace Fabricate.Tests.Application;

/// <summary>
/// #85: every member of a workspace shared one key, one bill and one quota. These cover the two personal rungs of
/// the resolver, who may manage a personal credential, and the two ways one stops being usable.
/// </summary>
public sealed class PersonalLlmCredentialTests
{
    private readonly InMemoryLlmCredentialStore _store = new();
    private readonly InMemoryWorkspaceRepository _workspaceRepo = new();
    private readonly InMemoryAuditLogRepository _auditRepo = new();
    private readonly LlmCredentialServiceTests.FakeCipher _cipher = new();
    private readonly NoSecrets _secrets = new();
    private readonly LlmOptions _options = new();
    private readonly WorkspaceService _workspaces;
    private readonly LlmCredentialService _service;

    private readonly Guid _accountId = Guid.NewGuid();
    private Guid _workspaceId;
    private Guid _adminId;
    private readonly Guid _memberId = Guid.NewGuid();
    private readonly Guid _otherMemberId = Guid.NewGuid();

    public PersonalLlmCredentialTests()
    {
        var audit = new AuditLogService(_auditRepo, new InMemoryAccountRepository());
        _workspaces = new WorkspaceService(_workspaceRepo, new InMemoryAccountGroupRepository(), audit);
        _service = new LlmCredentialService(
            _store, _cipher, _workspaces, audit, new AlwaysValidProbe(), new PromptDataBoundary(), _options);
    }

    private LlmCredentialResolver Resolver => new(_store, _cipher, _secrets, _workspaces, _options);

    private async Task SetUpWorkspaceAsync()
    {
        _adminId = Guid.NewGuid();
        var workspace = await _workspaces.CreateAsync(new CreateWorkspaceCommand(_accountId, "WS", _adminId));
        _workspaceId = workspace.Id;

        foreach (var member in new[] { _memberId, _otherMemberId })
        {
            await _workspaces.GrantAccessAsync(
                new GrantWorkspaceAccessCommand(_workspaceId, member, false, WorkspaceRole.Editor, _adminId));
        }
    }

    private Task<LlmCredentialSummary> RegisterPersonalAsync(Guid userId, string name = "mine", string secret = "sk-personal-0001", Guid? sessionId = null)
        => _service.RegisterAsync(
            new RegisterLlmCredentialCommand(
                _workspaceId, null, name, LlmProvider.Anthropic, LlmCredentialKind.ApiKey, secret, "claude-opus-5",
                IsPersonal: true, SessionId: sessionId),
            userId);

    private Task<LlmCredentialSummary> RegisterSharedAsync(string name = "shared", string secret = "sk-shared-0002")
        => _service.RegisterAsync(
            new RegisterLlmCredentialCommand(
                _workspaceId, null, name, LlmProvider.Anthropic, LlmCredentialKind.ApiKey, secret, "claude-opus-5",
                IsDefault: true),
            _adminId);

    // ── resolution ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AMembersOwnCredentialWins_WhileOtherMembersKeepTheSharedOne()
    {
        await SetUpWorkspaceAsync();
        await RegisterSharedAsync();
        await RegisterPersonalAsync(_memberId);

        var mine = await Resolver.ResolveAsync(_workspaceId, null, _memberId, null);
        var theirs = await Resolver.ResolveAsync(_workspaceId, null, _otherMemberId, null);

        mine!.Source.Should().Be(LlmCredentialSource.UserOwned);
        mine.GetSecret().Should().Be("sk-personal-0001");

        theirs!.Source.Should().Be(LlmCredentialSource.WorkspaceDefault);
        theirs.GetSecret().Should().Be("sk-shared-0002");
    }

    [Fact]
    public async Task ASessionBoundCredentialOutranksTheMembersWorkspaceWideOne()
    {
        await SetUpWorkspaceAsync();
        var sessionId = Guid.NewGuid();
        await RegisterPersonalAsync(_memberId, "workspace-wide", "sk-personal-0001");
        await RegisterPersonalAsync(_memberId, "for-this-session", "sk-session-0003", sessionId);

        var inSession = await Resolver.ResolveAsync(_workspaceId, null, _memberId, sessionId);
        var elsewhere = await Resolver.ResolveAsync(_workspaceId, null, _memberId, Guid.NewGuid());

        inSession!.Source.Should().Be(LlmCredentialSource.SessionBound);
        inSession.GetSecret().Should().Be("sk-session-0003");

        elsewhere!.Source.Should().Be(LlmCredentialSource.UserOwned);
        elsewhere.GetSecret().Should().Be("sk-personal-0001",
            "a session-bound credential belongs to that session and must not leak into others");
    }

    [Fact]
    public async Task WithoutAUserContext_ThePersonalRungsAreNeverReached()
    {
        await SetUpWorkspaceAsync();
        await RegisterSharedAsync();
        await RegisterPersonalAsync(_memberId);

        var resolved = await Resolver.ResolveAsync(_workspaceId, null);

        resolved!.Source.Should().Be(LlmCredentialSource.WorkspaceDefault);
    }

    [Fact]
    public async Task APersonalCredentialIsNeverPickedUpAsTheSingleWorkspaceCredential()
    {
        await SetUpWorkspaceAsync();
        await RegisterPersonalAsync(_memberId);

        // No shared credential exists at all — the workspace rungs must find nothing rather than spend a member's key.
        var forOther = await Resolver.ResolveAsync(_workspaceId, null, _otherMemberId, null);

        forOther.Should().BeNull("one member's personal key must not become everyone's fallback");
    }

    // ── governance ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisablingPersonalCredentialsBlocksNewOnesAndMakesExistingOnesUnresolvable()
    {
        await SetUpWorkspaceAsync();
        await RegisterSharedAsync();
        await RegisterPersonalAsync(_memberId);

        (await Resolver.ResolveAsync(_workspaceId, null, _memberId, null))!.Source
            .Should().Be(LlmCredentialSource.UserOwned);

        await _service.SetPolicyAsync(_workspaceId, false, _adminId, allowPersonalCredentials: false);

        (await Resolver.ResolveAsync(_workspaceId, null, _memberId, null))!.Source
            .Should().Be(LlmCredentialSource.WorkspaceDefault,
                "turning the switch off has to take effect immediately, or it is advice rather than a control");

        var register = async () => await RegisterPersonalAsync(_memberId, "another", "sk-personal-0009");
        (await register.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*does not permit personal LLM credentials*");
    }

    [Fact]
    public async Task LosingWorkspaceAccessMakesAPersonalCredentialUnresolvable()
    {
        await SetUpWorkspaceAsync();
        await RegisterSharedAsync();
        await RegisterPersonalAsync(_memberId);

        await _workspaces.RevokeAccessAsync(_workspaceId, _memberId, false, _adminId);

        var resolved = await Resolver.ResolveAsync(_workspaceId, null, _memberId, null);

        resolved!.Source.Should().Be(LlmCredentialSource.WorkspaceDefault,
            "access is checked at resolve time, because it can also be lost by a group membership changing");
    }

    // ── who may manage one ───────────────────────────────────────────────────────

    [Fact]
    public async Task AMemberCanRegisterAPersonalCredentialWithoutBeingAnAdmin()
    {
        await SetUpWorkspaceAsync();

        var summary = await RegisterPersonalAsync(_memberId);

        summary.OwnerUserId.Should().Be(_memberId);
        summary.IsPersonal.Should().BeTrue();
    }

    [Fact]
    public async Task ASharedCredentialStillRequiresAdmin()
    {
        await SetUpWorkspaceAsync();

        var register = async () => await _service.RegisterAsync(
            new RegisterLlmCredentialCommand(
                _workspaceId, null, "shared", LlmProvider.Anthropic, LlmCredentialKind.ApiKey, "sk-shared-0002", "claude-opus-5"),
            _memberId);

        await register.Should().ThrowAsync<UnauthorizedAccessException>(
            "a shared credential spends the workspace's quota");
    }

    [Fact]
    public async Task AnAdminSeesThatAPersonalCredentialExistsButCannotRotateOrUseIt()
    {
        await SetUpWorkspaceAsync();
        var personal = await RegisterPersonalAsync(_memberId);

        var listed = await _service.ListAsync(_workspaceId, _adminId);
        var seen = listed.Should().ContainSingle(c => c.Id == personal.Id).Subject;

        seen.OwnerUserId.Should().Be(_memberId, "governance needs to know whose it is");
        seen.Fingerprint.Should().NotBeNullOrWhiteSpace();
        System.Text.Json.JsonSerializer.Serialize(seen).Should().NotContain("sk-personal",
            "the summary is redacted, so listing discloses existence and nothing more");

        var rotate = async () => await _service.RotateAsync(_workspaceId, personal.Id, "sk-personal-0010", _adminId);
        var validate = async () => await _service.ValidateAsync(_workspaceId, personal.Id, _adminId);

        await rotate.Should().ThrowAsync<UnauthorizedAccessException>();
        await validate.Should().ThrowAsync<UnauthorizedAccessException>("validation spends the credential");
    }

    [Fact]
    public async Task OneMemberCannotSeeAnothersPersonalCredential()
    {
        await SetUpWorkspaceAsync();
        var personal = await RegisterPersonalAsync(_memberId);

        var listed = await _service.ListAsync(_workspaceId, _otherMemberId);

        listed.Should().NotContain(c => c.Id == personal.Id);
    }

    [Fact]
    public async Task AnAdminCanRevokeAPersonalCredentialForOffboarding()
    {
        await SetUpWorkspaceAsync();
        await RegisterSharedAsync();
        var personal = await RegisterPersonalAsync(_memberId);

        await _service.RevokeAsync(_workspaceId, personal.Id, _adminId);

        (await Resolver.ResolveAsync(_workspaceId, null, _memberId, null))!.Source
            .Should().Be(LlmCredentialSource.WorkspaceDefault,
                "offboarding has to be possible without the member's cooperation");
    }

    [Fact]
    public async Task TwoMembersMayUseTheSameCredentialName()
    {
        await SetUpWorkspaceAsync();

        await RegisterPersonalAsync(_memberId, "default", "sk-personal-0001");
        var second = async () => await RegisterPersonalAsync(_otherMemberId, "default", "sk-personal-0002");

        await second.Should().NotThrowAsync("names are unique within their own scope, not across members");
    }

    [Fact]
    public async Task RegisteringAPersonalCredentialIsAudited()
    {
        await SetUpWorkspaceAsync();
        await RegisterPersonalAsync(_memberId);

        _auditRepo.All.Should().Contain(e => e.Action == "llm_credential.registered" && e.ActorUserId == _memberId);
    }

    /// <summary>No platform credential is configured in these tests, so nothing ever reads a secret name.</summary>
    private sealed class NoSecrets : ISecretProvider
    {
        public Task<string> ResolveAsync(string secretName, CancellationToken cancellationToken = default)
            => throw new KeyNotFoundException(secretName);

        public Task<bool> ExistsAsync(string secretName, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }

    private sealed class AlwaysValidProbe : ILlmCredentialProbe
    {
        public Task<LlmCredentialValidationResult> ProbeAsync(Guid credentialId, ResolvedLlmCredential credential, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmCredentialValidationResult(credentialId, true, "ok", DateTimeOffset.UtcNow));
    }
}
