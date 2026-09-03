using Fabricate.Application.Llm;
using Fabricate.Domain.Models;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class LlmEndpointPolicyTests
{
    [Theory]
    [InlineData("http://api.example.com/v1", "https://")]
    [InlineData("ftp://api.example.com", "https://")]
    [InlineData("not a url", "absolute URL")]
    [InlineData("https://user:pass@api.example.com/v1", "embed credentials")]
    [InlineData("https://localhost:11434/v1", "public host")]
    [InlineData("https://127.0.0.1/v1", "public host")]
    [InlineData("https://10.0.0.5/v1", "public host")]
    [InlineData("https://172.16.4.1/v1", "public host")]
    [InlineData("https://192.168.1.10/v1", "public host")]
    [InlineData("https://169.254.169.254/latest/meta-data", "public host")]
    [InlineData("https://[::1]/v1", "public host")]
    [InlineData("https://[fe80::1]/v1", "public host")]
    [InlineData("https://gateway.internal/v1", "public host")]
    [InlineData("https://ollama/v1", "public host")]
    public void Validate_RejectsUnsafeEndpoints(string endpoint, string expectedFragment)
    {
        var act = () => LlmEndpointPolicy.Validate(endpoint, [], allowPrivateEndpoints: false);

        act.Should().Throw<ArgumentException>().WithMessage($"*{expectedFragment}*");
    }

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://my-gateway.example.com:8443/openai/v1")]
    [InlineData("https://8.8.8.8/v1")]
    public void Validate_AcceptsPublicHttpsEndpoints(string endpoint)
    {
        var uri = LlmEndpointPolicy.Validate(endpoint, [], allowPrivateEndpoints: false);

        uri.Scheme.Should().Be("https");
    }

    [Fact]
    public void Validate_AllowlistRestrictsHosts_WithSubdomainMatch()
    {
        string[] allowed = ["openai.com", "azure.com"];

        LlmEndpointPolicy.Validate("https://api.openai.com/v1", allowed, false).Host.Should().Be("api.openai.com");
        LlmEndpointPolicy.Validate("https://myres.openai.azure.com/", allowed, false).Host.Should().Be("myres.openai.azure.com");

        var act = () => LlmEndpointPolicy.Validate("https://api.openrouter.ai/v1", allowed, false);
        act.Should().Throw<ArgumentException>().WithMessage("*not in the allowed endpoint hosts*");

        var lookalike = () => LlmEndpointPolicy.Validate("https://notopenai.com/v1", allowed, false);
        lookalike.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_AllowPrivateEndpoints_PermitsLocalRuntimes()
    {
        LlmEndpointPolicy.Validate("http://localhost:11434/v1", [], allowPrivateEndpoints: true).Port.Should().Be(11434);
        LlmEndpointPolicy.Validate("http://10.0.0.5:8000/v1", [], allowPrivateEndpoints: true).Host.Should().Be("10.0.0.5");
    }
}

public sealed class LlmOptionsTests
{
    private static LlmOptions FromEnv(params (string key, string value)[] pairs)
    {
        // Later pairs override earlier ones so a test can start from a valid baseline and break one variable.
        var env = new Dictionary<string, string>();
        foreach (var (key, value) in pairs) env[LlmOptions.EnvPrefix + key] = value;
        return LlmOptions.FromEnvironment(k => env.GetValueOrDefault(k));
    }

    [Fact]
    public void Unset_MeansDisabled_AndValid()
    {
        var options = FromEnv();

        options.IsPlatformCredentialConfigured.Should().BeFalse();
        options.Validate().Should().BeEmpty();
    }

    [Fact]
    public void FullAnthropicConfiguration_Parses()
    {
        var options = FromEnv(
            ("PROVIDER", "anthropic"), ("MODEL", "claude-opus-5"), ("ALLOWED_MODELS", "claude-opus-5, claude-sonnet-5"),
            ("API_KEY_SECRET", "ANTHROPIC_API_KEY"), ("EFFORT", "high"), ("MAX_OUTPUT_TOKENS", "8000"), ("MAX_INPUT_TOKENS", "50000"),
            ("PLATFORM_FALLBACK", "always"), ("ALLOWED_ENDPOINT_HOSTS", "openai.com,anthropic.com"), ("ALLOW_PRIVATE_ENDPOINTS", "true"));

        options.Validate().Should().BeEmpty();
        options.ParsedProvider.Should().Be(LlmProvider.Anthropic);
        options.AllowedModels.Should().Equal("claude-opus-5", "claude-sonnet-5");
        options.Effort.Should().Be(LlmEffort.High);
        options.MaxOutputTokens.Should().Be(8000);
        options.MaxInputTokens.Should().Be(50000);
        options.PlatformFallback.Should().Be(PlatformFallbackMode.Always);
        options.AllowedEndpointHosts.Should().Equal("openai.com", "anthropic.com");
        options.AllowPrivateEndpoints.Should().BeTrue();
    }

    [Theory]
    [InlineData("PROVIDER", "carrier-pigeon", "not recognised")]
    [InlineData("MODEL", "", "MODEL is required")]
    [InlineData("ALLOWED_MODELS", "", "ALLOWED_MODELS is required")]
    [InlineData("MODEL", "claude-haiku-4-5", "not in FABRICATE_LLM_ALLOWED_MODELS")]
    [InlineData("API_KEY_SECRET", "", "API_KEY_SECRET")]
    [InlineData("PLATFORM_FALLBACK", "sometimes", "PLATFORM_FALLBACK must be one of")]
    public void Validate_NamesTheOffendingVariable(string key, string value, string expectedFragment)
    {
        var options = FromEnv(
            ("PROVIDER", "anthropic"), ("MODEL", "claude-opus-5"), ("ALLOWED_MODELS", "claude-opus-5"), ("API_KEY_SECRET", "ANTHROPIC_API_KEY"),
            (key, value));

        options.Validate().Should().ContainSingle(e => e.Contains(expectedFragment));
    }

    [Theory]
    [InlineData("openai-compatible", "BASE_URL is required")]
    [InlineData("bedrock", "REGION is required")]
    [InlineData("vertex", "PROJECT_ID")]
    [InlineData("foundry", "BASE_URL")]
    public void Validate_ProviderSpecificRequirements(string provider, string expectedFragment)
    {
        var options = FromEnv(("PROVIDER", provider), ("MODEL", "m"), ("ALLOWED_MODELS", "m"), ("API_KEY_SECRET", "KEY"));

        options.Validate().Should().Contain(e => e.Contains(expectedFragment));
    }

    [Fact]
    public void Validate_NeverEchoesSecretValues()
    {
        var options = FromEnv(("PROVIDER", "anthropic"), ("MODEL", "claude-opus-5"), ("ALLOWED_MODELS", "claude-opus-5"), ("API_KEY_SECRET", "MY_SECRET_VAR"));

        string.Join("\n", options.Validate()).Should().NotContain("sk-");
    }
}
