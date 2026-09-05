using Fabricate.Application.Abstractions;
using Fabricate.Application.Governance;
using Fabricate.Application.Llm;
using Fabricate.Application.Workspaces;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class LlmCredentialServiceTests
{
    private const string Secret = "sk-ant-api03-SUPERSECRETVALUE-9f8e7d6c";

    private readonly InMemoryAuditLogRepository _auditRepo = new();
    private readonly WorkspaceService _workspaceService;
    private readonly InMemoryLlmCredentialStore _store = new();
    private readonly FakeCipher _cipher = new();
    private readonly FakeProbe _probe = new();
    private readonly LlmOptions _options = new();
    private readonly LlmCredentialService _service;

    public LlmCredentialServiceTests()
    {
        var audit = new AuditLogService(_auditRepo, new InMemoryAccountRepository());
        _workspaceService = new WorkspaceService(new InMemoryWorkspaceRepository(), new InMemoryAccountGroupRepository(), audit);
        _service = new LlmCredentialService(_store, _cipher, _workspaceService, audit, _probe, _options);
    }

    private async Task<(Guid wsId, Guid adminId, Guid editorId, Guid viewerId)> CreateWorkspaceAsync()
    {
        var adminId = Guid.NewGuid();
        var ws = await _workspaceService.CreateAsync(new CreateWorkspaceCommand(Guid.NewGuid(), "WS", adminId));
        var editorId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        await _workspaceService.GrantAccessAsync(new GrantWorkspaceAccessCommand(ws.Id, editorId, false, WorkspaceRole.Editor, adminId));
        await _workspaceService.GrantAccessAsync(new GrantWorkspaceAccessCommand(ws.Id, viewerId, false, WorkspaceRole.Viewer, adminId));
        return (ws.Id, adminId, editorId, viewerId);
    }

    private static RegisterLlmCredentialCommand Register(Guid wsId, string name = "primary", string secret = Secret, LlmProvider provider = LlmProvider.Anthropic, string? endpoint = null, bool isDefault = false, Guid? projectId = null)
        => new(wsId, projectId, name, provider, LlmCredentialKind.ApiKey, secret, "claude-opus-5", endpoint, null, isDefault);

    [Fact]
    public async Task Register_StoresCiphertextOnly_AndReturnsRedactedSummary()
    {
        var (wsId, adminId, _, _) = await CreateWorkspaceAsync();

        var summary = await _service.RegisterAsync(Register(wsId), adminId);

        summary.LastFour.Should().Be("7d6c");
        summary.Fingerprint.Should().HaveLength(16);
        summary.Status.Should().Be(LlmCredentialStatus.Active);

        var stored = (await _store.GetByIdAsync(summary.Id))!;
        stored.CipherText.Should().NotContain(Secret);
        _cipher.Decrypt(stored.CipherText, stored.KeyVersion).Should().Be(Secret);

        NoPlaintextIn(summary);
    }

    [Fact]
    public async Task Register_RejectsDuplicateNames_EmptySecret_AndDisallowedModel()
    {
        var (wsId, adminId, _, _) = await CreateWorkspaceAsync();
        await _service.RegisterAsync(Register(wsId, "dup"), adminId);

        await FluentActions.Invoking(() => _service.RegisterAsync(Register(wsId, "DUP"), adminId))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*already exists*");

        await FluentActions.Invoking(() => _service.RegisterAsync(Register(wsId, "empty", secret: "  "), adminId))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*Secret is required*");

        _options.AllowedModels = ["claude-sonnet-5"];
        await FluentActions.Invoking(() => _service.RegisterAsync(Register(wsId, "model"), adminId))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*not in the instance allowlist*");
    }

    [Fact]
    public async Task Register_RequiresEndpointForOpenAiCompatible_AndAppliesEgressPolicy()
    {
        var (wsId, adminId, _, _) = await CreateWorkspaceAsync();

        await FluentActions.Invoking(() => _service.RegisterAsync(Register(wsId, "oa", provider: LlmProvider.OpenAiCompatible), adminId))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*Endpoint is required*");

        await FluentActions.Invoking(() => _service.RegisterAsync(Register(wsId, "oa", provider: LlmProvider.OpenAiCompatible, endpoint: "http://169.254.169.254/latest"), adminId))
            .Should().ThrowAsync<ArgumentException>();

        var ok = await _service.RegisterAsync(Register(wsId, "oa", provider: LlmProvider.OpenAiCompatible, endpoint: "https://api.openai.com/v1"), adminId);
        ok.Endpoint.Should().Be("https://api.openai.com/v1");
    }

    [Fact]
    public async Task AuthorizationMatrix_AdminMutates_MembersRead_OutsidersDenied()
    {
        var (wsId, adminId, editorId, viewerId) = await CreateWorkspaceAsync();
        var outsiderId = Guid.NewGuid();

        var created = await _service.RegisterAsync(Register(wsId), adminId);

        await FluentActions.Invoking(() => _service.RegisterAsync(Register(wsId, "e"), editorId)).Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Invoking(() => _service.RotateAsync(wsId, created.Id, "new-secret-xyz", editorId)).Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Invoking(() => _service.RevokeAsync(wsId, created.Id, viewerId)).Should().ThrowAsync<UnauthorizedAccessException>();
        await FluentActions.Invoking(() => _service.SetPolicyAsync(wsId, true, editorId)).Should().ThrowAsync<UnauthorizedAccessException>();

        (await _service.ListAsync(wsId, viewerId)).Should().ContainSingle();
        (await _service.ValidateAsync(wsId, created.Id, editorId)).IsValid.Should().BeTrue();

        await FluentActions.Invoking(() => _service.ListAsync(wsId, outsiderId)).Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task CrossWorkspaceCredentialId_IsNotFound_NotForbidden()
    {
        var (wsA, adminA, _, _) = await CreateWorkspaceAsync();
        var (wsB, adminB, _, _) = await CreateWorkspaceAsync();
        var created = await _service.RegisterAsync(Register(wsA), adminA);

        await FluentActions.Invoking(() => _service.RotateAsync(wsB, created.Id, "x", adminB)).Should().ThrowAsync<KeyNotFoundException>();
        await FluentActions.Invoking(() => _service.RevokeAsync(wsB, created.Id, adminB)).Should().ThrowAsync<KeyNotFoundException>();
        await FluentActions.Invoking(() => _service.ValidateAsync(wsB, created.Id, adminB)).Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Rotate_ReplacesCiphertextAndFingerprint()
    {
        var (wsId, adminId, _, _) = await CreateWorkspaceAsync();
        var created = await _service.RegisterAsync(Register(wsId), adminId);
        var before = (await _store.GetByIdAsync(created.Id))!;

        var rotated = await _service.RotateAsync(wsId, created.Id, "sk-rotated-value-0001", adminId);

        rotated.LastFour.Should().Be("0001");
        rotated.Fingerprint.Should().NotBe(before.Fingerprint);
        var after = (await _store.GetByIdAsync(created.Id))!;
        _cipher.Decrypt(after.CipherText, after.KeyVersion).Should().Be("sk-rotated-value-0001");
    }

    [Fact]
    public async Task Revoke_IsSoft_AndRevokedCredentialsCannotBeRotatedOrValidated()
    {
        var (wsId, adminId, _, _) = await CreateWorkspaceAsync();
        var created = await _service.RegisterAsync(Register(wsId), adminId);

        await _service.RevokeAsync(wsId, created.Id, adminId);

        var listed = (await _service.ListAsync(wsId, adminId)).Single();
        listed.Status.Should().Be(LlmCredentialStatus.Revoked);
        listed.RevokedAt.Should().NotBeNull();
        (await _store.GetByIdAsync(created.Id))!.CipherText.Should().NotBeEmpty("audit trail is retained");

        await FluentActions.Invoking(() => _service.RotateAsync(wsId, created.Id, "x", adminId)).Should().ThrowAsync<InvalidOperationException>();
        (await _service.ValidateAsync(wsId, created.Id, adminId)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_UsesProbe_UpdatesStatus_AndNeverExposesSecret()
    {
        var (wsId, adminId, _, _) = await CreateWorkspaceAsync();
        var created = await _service.RegisterAsync(Register(wsId), adminId);

        _probe.NextResult = (false, "401 unauthorized");
        var result = await _service.ValidateAsync(wsId, created.Id, adminId);

        result.IsValid.Should().BeFalse();
        result.Message.Should().Be("401 unauthorized");
        (await _service.ListAsync(wsId, adminId)).Single().Status.Should().Be(LlmCredentialStatus.Invalid);
        _probe.LastCredential!.GetSecret().Should().Be(Secret, "the probe is the one legitimate consumer of plaintext");
        _probe.LastCredential.ToString().Should().NotContain(Secret);
    }

    [Fact]
    public async Task Register_WithIsDefault_ClearsPreviousDefaultForSameProvider()
    {
        var (wsId, adminId, _, _) = await CreateWorkspaceAsync();
        var first = await _service.RegisterAsync(Register(wsId, "a", isDefault: true), adminId);
        var second = await _service.RegisterAsync(Register(wsId, "b", isDefault: true), adminId);

        var listed = await _service.ListAsync(wsId, adminId);
        listed.Single(c => c.Id == first.Id).IsDefault.Should().BeFalse();
        listed.Single(c => c.Id == second.Id).IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task EveryMutation_IsAudited_WithoutSecret()
    {
        var (wsId, adminId, _, _) = await CreateWorkspaceAsync();
        var created = await _service.RegisterAsync(Register(wsId), adminId);
        await _service.RotateAsync(wsId, created.Id, "sk-rotated-0002", adminId);
        await _service.ValidateAsync(wsId, created.Id, adminId);
        await _service.SetPolicyAsync(wsId, true, adminId);
        await _service.RevokeAsync(wsId, created.Id, adminId);

        var events = _auditRepo.All.Where(e => e.Action.StartsWith("llm_", StringComparison.Ordinal)).ToArray();
        events.Select(e => e.Action).Should().Contain(["llm_credential.registered", "llm_credential.rotated", "llm_credential.validated", "llm_policy.updated", "llm_credential.revoked"]);
        foreach (var e in events)
        {
            (e.Details ?? string.Empty).Should().NotContain(Secret).And.NotContain("sk-rotated-0002");
        }
    }

    [Fact]
    public async Task Policy_DefaultsToNoPlatformFallback_AndCanBeSetByAdmin()
    {
        var (wsId, adminId, _, viewerId) = await CreateWorkspaceAsync();

        (await _service.GetPolicyAsync(wsId, viewerId)).AllowPlatformFallback.Should().BeFalse();
        await _service.SetPolicyAsync(wsId, true, adminId);
        (await _service.GetPolicyAsync(wsId, viewerId)).AllowPlatformFallback.Should().BeTrue();
    }

    [Fact]
    public async Task Policy_ToolAllowlist_IsPersisted_Normalised_AndPreservedWhenOmitted()
    {
        var (wsId, adminId, _, viewerId) = await CreateWorkspaceAsync();

        (await _service.GetPolicyAsync(wsId, viewerId)).AllowedTools.Should().BeNull("no policy means every registered tool");

        await _service.SetPolicyAsync(wsId, false, adminId, [" discover_schema ", "generate_data", "GENERATE_DATA", ""]);
        (await _service.GetPolicyAsync(wsId, viewerId)).AllowedTools.Should().Equal("discover_schema", "generate_data");

        await _service.SetPolicyAsync(wsId, true, adminId);
        var after = await _service.GetPolicyAsync(wsId, viewerId);
        after.AllowPlatformFallback.Should().BeTrue();
        after.AllowedTools.Should().Equal(["discover_schema", "generate_data"], "omitting the list leaves it unchanged");

        await _service.SetPolicyAsync(wsId, true, adminId, []);
        (await _service.GetPolicyAsync(wsId, viewerId)).AllowedTools.Should().BeEmpty("an empty list means no tools");

        _auditRepo.All.Last(e => e.Action == "llm_policy.updated").Details.Should().Contain("allowedTools=");
    }

    private static void NoPlaintextIn(LlmCredentialSummary summary)
    {
        summary.ToString().Should().NotContain(Secret);
        System.Text.Json.JsonSerializer.Serialize(summary).Should().NotContain(Secret);
    }

    // ── Doubles ───────────────────────────────────────────────────────────────────

    /// <summary>Reversible, obviously-not-secure cipher for tests; the point is round-trip and key-version plumbing.</summary>
    internal sealed class FakeCipher : ISecretCipher
    {
        public (string CipherText, string KeyVersion) Encrypt(string plaintext)
            => ("enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext)), "test-v1");

        public string Decrypt(string cipherText, string keyVersion)
            => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cipherText["enc:".Length..]));
    }

    private sealed class FakeProbe : ILlmCredentialProbe
    {
        public (bool ok, string message) NextResult { get; set; } = (true, "ok");
        public ResolvedLlmCredential? LastCredential { get; private set; }

        public Task<LlmCredentialValidationResult> ProbeAsync(Guid credentialId, ResolvedLlmCredential credential, CancellationToken ct = default)
        {
            LastCredential = credential;
            return Task.FromResult(new LlmCredentialValidationResult(credentialId, NextResult.ok, NextResult.message, DateTimeOffset.UtcNow));
        }
    }
}
