using Fabricate.Domain.Models;

namespace Fabricate.Application.Llm;

/// <summary>When the operator's instance-level (platform) credential may be used for a workspace.</summary>
public enum PlatformFallbackMode
{
    /// <summary>Only workspaces that have opted in via <see cref="WorkspaceLlmPolicy"/>. The multi-tenant default.</summary>
    WorkspaceOptIn = 0,
    /// <summary>Every workspace without its own credential. The self-hosted, single-operator setting.</summary>
    Always,
    Never
}

/// <summary>
/// Instance-level LLM configuration, read from <c>FABRICATE_LLM_*</c> environment variables.
/// Leaving <see cref="Provider"/> unset disables the platform credential entirely; per-workspace credentials still work.
/// </summary>
public sealed class LlmOptions
{
    public const string EnvPrefix = "FABRICATE_LLM_";

    public string? Provider { get; set; }
    public string? Model { get; set; }
    public IReadOnlyList<string> AllowedModels { get; set; } = [];
    /// <summary>Name of the environment variable / secret holding the API key — never the key itself.</summary>
    public string? ApiKeySecretName { get; set; }
    public string? BaseUrl { get; set; }
    public string? Region { get; set; }
    public string? ProjectId { get; set; }
    public string? Location { get; set; }
    public int MaxOutputTokens { get; set; } = 16_000;
    public int TimeoutSeconds { get; set; } = 120;
    public LlmEffort? Effort { get; set; }
    public int MaxToolIterations { get; set; } = 8;
    public int HistoryWindow { get; set; } = 40;
    public PlatformFallbackMode PlatformFallback { get; set; } = PlatformFallbackMode.WorkspaceOptIn;
    /// <summary>Hosts that tenant-supplied endpoints may target. Empty means any public HTTPS host.</summary>
    public IReadOnlyList<string> AllowedEndpointHosts { get; set; } = [];
    /// <summary>Permit http:// and private/loopback endpoints — for air-gapped deployments talking to a local runtime.</summary>
    public bool AllowPrivateEndpoints { get; set; }

    public bool IsPlatformCredentialConfigured => !string.IsNullOrWhiteSpace(Provider);

    public LlmProvider? ParsedProvider => TryParseProvider(Provider, out var p) ? p : null;

    public static bool TryParseProvider(string? value, out LlmProvider provider)
    {
        switch (value?.Trim().ToLowerInvariant().Replace("_", "-", StringComparison.Ordinal))
        {
            case "anthropic": provider = LlmProvider.Anthropic; return true;
            case "openai-compatible" or "openai": provider = LlmProvider.OpenAiCompatible; return true;
            case "bedrock" or "aws-bedrock": provider = LlmProvider.AwsBedrock; return true;
            case "vertex" or "gcp-vertex-ai" or "vertex-ai": provider = LlmProvider.GcpVertexAi; return true;
            case "foundry" or "azure-foundry": provider = LlmProvider.AzureFoundry; return true;
            default: provider = default; return false;
        }
    }

    public static LlmOptions FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        string? Get(string suffix) => getEnvironmentVariable(EnvPrefix + suffix);

        var options = new LlmOptions
        {
            Provider = Get("PROVIDER"),
            Model = Get("MODEL"),
            ApiKeySecretName = Get("API_KEY_SECRET"),
            BaseUrl = Get("BASE_URL"),
            Region = Get("REGION"),
            ProjectId = Get("PROJECT_ID"),
            Location = Get("LOCATION"),
        };

        var allowed = Get("ALLOWED_MODELS");
        if (!string.IsNullOrWhiteSpace(allowed))
        {
            options.AllowedModels = allowed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (int.TryParse(Get("MAX_OUTPUT_TOKENS"), out var maxTokens)) options.MaxOutputTokens = maxTokens;
        if (int.TryParse(Get("TIMEOUT_SECONDS"), out var timeout)) options.TimeoutSeconds = timeout;
        if (int.TryParse(Get("MAX_TOOL_ITERATIONS"), out var iterations)) options.MaxToolIterations = iterations;
        if (int.TryParse(Get("HISTORY_WINDOW"), out var window)) options.HistoryWindow = window;
        if (Enum.TryParse<LlmEffort>(Get("EFFORT"), ignoreCase: true, out var effort)) options.Effort = effort;
        if (bool.TryParse(Get("ALLOW_PRIVATE_ENDPOINTS"), out var allowPrivate)) options.AllowPrivateEndpoints = allowPrivate;

        var hosts = Get("ALLOWED_ENDPOINT_HOSTS");
        if (!string.IsNullOrWhiteSpace(hosts))
        {
            options.AllowedEndpointHosts = hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        switch (Get("PLATFORM_FALLBACK")?.Trim().ToLowerInvariant())
        {
            case "always": options.PlatformFallback = PlatformFallbackMode.Always; break;
            case "never": options.PlatformFallback = PlatformFallbackMode.Never; break;
            case "workspace-opt-in" or "opt-in" or null or "": break;
            default: options.PlatformFallback = (PlatformFallbackMode)(-1); break;
        }

        return options;
    }

    /// <summary>Returns configuration errors. Empty when the platform credential is unset (disabled) or fully valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!Enum.IsDefined(PlatformFallback))
        {
            errors.Add($"{EnvPrefix}PLATFORM_FALLBACK must be one of: workspace-opt-in, always, never.");
        }

        if (!IsPlatformCredentialConfigured)
        {
            return errors;
        }

        if (!TryParseProvider(Provider, out var provider))
        {
            errors.Add($"{EnvPrefix}PROVIDER '{Provider}' is not recognised. Supported: anthropic, openai-compatible, bedrock, vertex, foundry.");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(Model))
        {
            errors.Add($"{EnvPrefix}MODEL is required when {EnvPrefix}PROVIDER is set.");
        }

        if (AllowedModels.Count == 0)
        {
            errors.Add($"{EnvPrefix}ALLOWED_MODELS is required when {EnvPrefix}PROVIDER is set.");
        }
        else if (!string.IsNullOrWhiteSpace(Model) && !AllowedModels.Contains(Model, StringComparer.Ordinal))
        {
            errors.Add($"{EnvPrefix}MODEL '{Model}' is not in {EnvPrefix}ALLOWED_MODELS.");
        }

        switch (provider)
        {
            case LlmProvider.Anthropic or LlmProvider.AzureFoundry when string.IsNullOrWhiteSpace(ApiKeySecretName):
                errors.Add($"{EnvPrefix}API_KEY_SECRET (the name of the variable holding the key) is required for provider '{Provider}'.");
                break;
            case LlmProvider.OpenAiCompatible when string.IsNullOrWhiteSpace(BaseUrl):
                errors.Add($"{EnvPrefix}BASE_URL is required for provider 'openai-compatible'.");
                break;
            case LlmProvider.AwsBedrock when string.IsNullOrWhiteSpace(Region):
                errors.Add($"{EnvPrefix}REGION is required for provider 'bedrock'.");
                break;
            case LlmProvider.GcpVertexAi when string.IsNullOrWhiteSpace(ProjectId) || string.IsNullOrWhiteSpace(Location):
                errors.Add($"{EnvPrefix}PROJECT_ID and {EnvPrefix}LOCATION are required for provider 'vertex'.");
                break;
            case LlmProvider.AzureFoundry when string.IsNullOrWhiteSpace(BaseUrl):
                errors.Add($"{EnvPrefix}BASE_URL (the Foundry resource endpoint) is required for provider 'foundry'.");
                break;
        }

        if (MaxOutputTokens <= 0) errors.Add($"{EnvPrefix}MAX_OUTPUT_TOKENS must be positive.");
        if (TimeoutSeconds <= 0) errors.Add($"{EnvPrefix}TIMEOUT_SECONDS must be positive.");
        if (MaxToolIterations <= 0) errors.Add($"{EnvPrefix}MAX_TOOL_ITERATIONS must be positive.");

        return errors;
    }

    public bool IsModelAllowed(string model)
        => AllowedModels.Count == 0 || AllowedModels.Contains(model, StringComparer.Ordinal);
}
