using Fabricate.Application.Abstractions;
using Fabricate.Application.Llm;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Repositories;
using FluentAssertions;

namespace Fabricate.Tests.Application;

/// <summary>One test per rung of the resolution precedence chain, plus the platform-fallback policy modes.</summary>
public sealed class LlmCredentialResolverTests
{
    private readonly InMemoryLlmCredentialStore _store = new();
    private readonly LlmCredentialServiceTests.FakeCipher _cipher = new();
    private readonly DictionarySecretProvider _secrets = new();
    private readonly LlmOptions _options = new();
    private readonly Guid _wsId = Guid.NewGuid();
    private readonly Guid _projectId = Guid.NewGuid();

    private LlmCredentialResolver Resolver => new(_store, _cipher, _secrets, _options);

    private async Task<LlmCredential> AddAsync(string name, string secret, Guid? projectId = null, bool isDefault = false, LlmProvider provider = LlmProvider.Anthropic, bool revoked = false, DateTimeOffset? createdAt = null)
    {
        var (cipherText, keyVersion) = _cipher.Encrypt(secret);
        var credential = new LlmCredential(Guid.NewGuid(), _wsId, projectId, name, provider, LlmCredentialKind.ApiKey, cipherText, keyVersion,
            "fp", secret[^4..], null, "claude-opus-5", new Dictionary<string, string>(), isDefault,
            revoked ? LlmCredentialStatus.Revoked : LlmCredentialStatus.Active,
            createdAt ?? DateTimeOffset.UtcNow, Guid.NewGuid(), RevokedAt: revoked ? DateTimeOffset.UtcNow : null);
        return await _store.SaveAsync(credential);
    }

    private void ConfigurePlatform(PlatformFallbackMode mode)
    {
        _options.Provider = "anthropic";
        _options.Model = "claude-opus-5";
        _options.AllowedModels = ["claude-opus-5"];
        _options.ApiKeySecretName = "ANTHROPIC_API_KEY";
        _options.PlatformFallback = mode;
        _secrets.Values["ANTHROPIC_API_KEY"] = "platform-key-0000";
    }

    [Fact]
    public async Task Rung1_ProjectScopedCredentialWins()
    {
        await AddAsync("ws-default", "ws-secret-1111", isDefault: true);
        await AddAsync("project", "proj-secret-2222", projectId: _projectId);

        var resolved = await Resolver.ResolveAsync(_wsId, _projectId);

        resolved!.Source.Should().Be(LlmCredentialSource.Project);
        resolved.GetSecret().Should().Be("proj-secret-2222");
    }

    [Fact]
    public async Task Rung2_WorkspaceDefaultForProvider_WhenNoProjectMatch()
    {
        await AddAsync("other", "other-secret-1111", createdAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        await AddAsync("default", "default-secret-2222", isDefault: true);

        var resolved = await Resolver.ResolveAsync(_wsId, _projectId);

        resolved!.Source.Should().Be(LlmCredentialSource.WorkspaceDefault);
        resolved.GetSecret().Should().Be("default-secret-2222");
    }

    [Fact]
    public async Task Rung2_PreferredProvider_SelectsMatchingDefault()
    {
        await AddAsync("anthropic-default", "a-secret-1111", isDefault: true, provider: LlmProvider.Anthropic);
        await AddAsync("openai-default", "o-secret-2222", isDefault: true, provider: LlmProvider.OpenAiCompatible);

        var resolved = await Resolver.ResolveAsync(_wsId, null, LlmProvider.OpenAiCompatible);

        resolved!.Provider.Should().Be(LlmProvider.OpenAiCompatible);
    }

    [Fact]
    public async Task Rung3_SingleActiveWorkspaceCredential_IsUsedWithoutDefaultFlag()
    {
        await AddAsync("only", "only-secret-1111");
        await AddAsync("revoked", "gone-secret-2222", revoked: true);

        var resolved = await Resolver.ResolveAsync(_wsId, null);

        resolved!.Source.Should().Be(LlmCredentialSource.WorkspaceSingle);
        resolved.GetSecret().Should().Be("only-secret-1111");
    }

    [Fact]
    public async Task Rung3_MultipleNonDefaultCredentials_IsAmbiguous_FallsThrough()
    {
        await AddAsync("a", "a-secret-1111");
        await AddAsync("b", "b-secret-2222");

        (await Resolver.ResolveAsync(_wsId, null)).Should().BeNull();
    }

    [Fact]
    public async Task Rung4_PlatformFallback_OnlyWhenWorkspaceOptsIn()
    {
        ConfigurePlatform(PlatformFallbackMode.WorkspaceOptIn);

        (await Resolver.ResolveAsync(_wsId, null)).Should().BeNull("default policy is opt-out");

        await _store.SavePolicyAsync(new WorkspaceLlmPolicy(_wsId, true, DateTimeOffset.UtcNow));
        var resolved = await Resolver.ResolveAsync(_wsId, null);

        resolved!.Source.Should().Be(LlmCredentialSource.Platform);
        resolved.GetSecret().Should().Be("platform-key-0000");
        resolved.Model.Should().Be("claude-opus-5");
    }

    [Fact]
    public async Task Rung4_PlatformFallbackAlways_AppliesWithoutOptIn()
    {
        ConfigurePlatform(PlatformFallbackMode.Always);

        (await Resolver.ResolveAsync(_wsId, null))!.Source.Should().Be(LlmCredentialSource.Platform);
    }

    [Fact]
    public async Task Rung4_PlatformFallbackNever_IgnoresOptIn()
    {
        ConfigurePlatform(PlatformFallbackMode.Never);
        await _store.SavePolicyAsync(new WorkspaceLlmPolicy(_wsId, true, DateTimeOffset.UtcNow));

        (await Resolver.ResolveAsync(_wsId, null)).Should().BeNull();
    }

    [Fact]
    public async Task Rung4_PlatformCredentialWithMissingSecret_ResolvesToNull()
    {
        ConfigurePlatform(PlatformFallbackMode.Always);
        _secrets.Values.Clear();

        (await Resolver.ResolveAsync(_wsId, null)).Should().BeNull();
    }

    [Fact]
    public async Task Rung5_NothingConfigured_ReturnsNull()
    {
        (await Resolver.ResolveAsync(_wsId, _projectId)).Should().BeNull();
    }

    [Fact]
    public async Task RevokedCredentials_AreNeverResolved()
    {
        await AddAsync("revoked-default", "dead-secret-1111", isDefault: true, revoked: true);

        (await Resolver.ResolveAsync(_wsId, null)).Should().BeNull();
    }

    private sealed class DictionarySecretProvider : ISecretProvider
    {
        public Dictionary<string, string> Values { get; } = [];

        public Task<string> ResolveAsync(string secretName, CancellationToken ct = default)
            => Values.TryGetValue(secretName, out var v) ? Task.FromResult(v) : throw new InvalidOperationException($"Secret '{secretName}' not found.");

        public Task<bool> ExistsAsync(string secretName, CancellationToken ct = default)
            => Task.FromResult(Values.ContainsKey(secretName));
    }
}
