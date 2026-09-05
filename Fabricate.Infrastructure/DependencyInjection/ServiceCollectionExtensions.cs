using Fabricate.Application.Abstractions;
using Fabricate.Application.Accounts;
using Fabricate.Application.ApiKeys;
using Fabricate.Application.Chat;
using Fabricate.Application.Chat.Tools;
using Fabricate.Application.Compliance;
using Fabricate.Application.Configuration;
using Fabricate.Application.Constraints;
using Fabricate.Application.Generation;
using Fabricate.Application.Governance;
using Fabricate.Application.Llm;
using Fabricate.Application.Orchestration;
using Fabricate.Application.Planning;
using Fabricate.Application.Projects;
using Fabricate.Application.Schema;
using Fabricate.Application.Webhooks;
using Fabricate.Application.Workflows;
using Fabricate.Application.Workspaces;
using Fabricate.Infrastructure.Configuration;
using Fabricate.Infrastructure.Export;
using Fabricate.Infrastructure.Llm;
using Fabricate.Infrastructure.Repositories;
using Fabricate.Infrastructure.Schema;
using Fabricate.Infrastructure.Webhooks;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fabricate.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFabricateApplication(this IServiceCollection services, long seed)
    {
        services.AddSingleton<IRandomService>(_ => new DeterministicRandomService(seed));

        // Register the concrete GeneratorRegistry as a singleton, initializing defaults in the factory.
        // Both IGeneratorRegistry and IValueGeneratorDispatcher forward to the same instance to avoid
        // a circular dependency and fragile cast.
        services.AddSingleton<GeneratorRegistry>(sp =>
        {
            var random = sp.GetRequiredService<IRandomService>();
            var registry = new GeneratorRegistry();
            registry.RegisterDefaults(random);
            return registry;
        });
        services.AddSingleton<IGeneratorRegistry>(sp => sp.GetRequiredService<GeneratorRegistry>());
        services.AddSingleton<IValueGeneratorDispatcher>(sp => sp.GetRequiredService<GeneratorRegistry>());

        services.AddSingleton<IConstraintEvaluator, ConstraintEvaluator>();
        services.AddSingleton<IGenerationPlanner, DependencyGraphPlanner>();
        services.AddSingleton<ISensitiveFieldPolicy, DefaultSensitiveFieldPolicy>();
        services.AddSingleton<IRuleConfigurationService, RuleConfigurationService>();
        services.AddSingleton<ISchemaDiscoveryService, SchemaDiscoveryService>();
        services.AddSingleton<ReferentialRowMaterializer>();
        services.AddSingleton<IRowMaterializer>(sp => sp.GetRequiredService<ReferentialRowMaterializer>());
        services.AddSingleton<IRowMaterializerStream>(sp => sp.GetRequiredService<ReferentialRowMaterializer>());
        services.AddSingleton<ISyntheticDataOrchestrator, SyntheticDataOrchestrator>();
        services.AddSingleton<ISchemaReviewService, SchemaReviewService>();
        services.AddSingleton<IGenerationPlanService, GenerationPlanService>();
        services.AddScoped<RunLifecycleService>();
        // #66 — starting and reading runs through the API, scoped to a workspace the caller belongs to.
        services.AddScoped<IRunExecutionService, RunExecutionService>();

        // #26 — accounts
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IInvitationService, InvitationService>();
        services.AddScoped<IUserProfileService, UserProfileService>();

        // #27 — governance
        services.AddScoped<IAccountGroupService, AccountGroupService>();
        services.AddScoped<IAllowedDomainService, AllowedDomainService>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        // #83 — the prompt data boundary is stateless policy, so a singleton.
        services.TryAddSingleton<IPromptDataBoundary, PromptDataBoundary>();
        // Retention defaults to "keep everything" (#74); the host overrides this from the environment.
        services.TryAddSingleton(new AuditRetentionOptions());
        services.TryAddSingleton(TimeProvider.System);

        // #28 — workspaces
        services.AddScoped<IWorkspaceService, WorkspaceService>();
        services.AddScoped<IConnectionCatalogService, ConnectionCatalogService>();
        // #69 — a schema provider per workspace connection, instead of the one instance-level provider.
        services.TryAddSingleton<ISchemaProviderFactory, SchemaProviderFactory>();
        services.AddScoped<IConnectionResolver, ConnectionResolver>();
        services.AddScoped<IInstructionVersionService, InstructionVersionService>();

        // #29 — projects
        services.AddSingleton<IProjectRepository, InMemoryProjectRepository>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<IProjectDatabaseCatalog, ProjectDatabaseCatalog>();

        // #30 — chat
        services.AddSingleton<IToolRegistry>(sp =>
        {
            var registry = new ToolRegistry();
            // Built-in tools registered at composition time
            // The resolver opens a scope per call: this registry is a singleton, and the connection repository
            // and cipher are scoped once a database provider is configured (#69).
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            registry.Register(new DiscoverSchemaTool(
                sp.GetRequiredService<ISchemaDiscoveryService>(),
                async (sessionId, ct) =>
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var sessions = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
                    var session = await sessions.GetByIdAsync(sessionId, ct).ConfigureAwait(false);
                    if (session is null) return null;

                    var connections = scope.ServiceProvider.GetRequiredService<IConnectionResolver>();
                    return await connections.ResolveAsync(session.WorkspaceId, session.ProjectId, ct).ConfigureAwait(false);
                }));
            registry.Register(new GenerateDataTool(sp.GetRequiredService<ISyntheticDataOrchestrator>()));
            return registry;
        });
        services.AddScoped<IAgentChatService, AgentChatService>();

        // #31 — API keys
        services.AddScoped<IApiKeyService, ApiKeyService>();

        // #24 — workflows
        services.AddScoped<IWorkflowService, WorkflowService>();
        services.AddScoped<ISkillRegistry, SkillRegistryService>();
        services.AddSingleton<IApiContractIngestionService, OpenApiContractIngestionService>();

        // #13 — schema/profile snapshots
        // Snapshot services still hold their own in-memory state (no repository yet — see #75), so they stay singleton:
        // scoping them would discard snapshots between requests. They consume nothing scoped, so no captive dependency.
        // Scoped, not singleton: these now read through repositories, which AddFabricatePersistence makes scoped.
        // A singleton here would capture one DbContext for the process (#78).
        services.AddScoped<ISchemaSnapshotService, SchemaSnapshotService>();
        services.AddScoped<IProfileSnapshotService, ProfileSnapshotService>();

        // #43 — webhooks
        services.AddScoped<IWebhookService, WebhookService>();

        return services;
    }

    public static IServiceCollection AddFabricateInfrastructure(this IServiceCollection services, Action<SchemaProviderOptions> configure)
    {
        services.Configure(configure);
        services.AddSingleton<ISchemaProvider>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SchemaProviderOptions>>().Value;
            return options.Provider.ToLowerInvariant() switch
            {
                "postgres" or "postgresql" => ActivatorUtilities.CreateInstance<PostgreSqlSchemaProvider>(sp),
                "sqlite" => ActivatorUtilities.CreateInstance<SqliteSchemaProvider>(sp),
                _ => throw new InvalidOperationException(
                    $"Unsupported schema provider '{options.Provider}'. Supported values: sqlite, postgres, postgresql.")
            };
        });

        services.AddSingleton<IExporter, CsvExporter>();
        services.AddSingleton<IExporter, JsonExporter>();
        services.AddSingleton<IExporter, SqlExporter>();
        services.AddSingleton<IExporter, ParquetExporter>();

        // Generated artifacts go to FABRICATE_ARTIFACTS_PATH when set, else the OS temp directory. On hosted platforms
        // the container filesystem is ephemeral either way; point this at a mounted volume if artifacts must outlive
        // a restart (object storage is tracked as a follow-up on #61).
        services.AddSingleton<IArtifactStore>(_ =>
        {
            var configured = Environment.GetEnvironmentVariable("FABRICATE_ARTIFACTS_PATH");
            var baseDir = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Path.GetTempPath(), "fabricate-artifacts")
                : configured;
            return new FileSystemArtifactStore(baseDir);
        });

        services.AddSingleton<IDataProfiler>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SchemaProviderOptions>>().Value;
            return options.Provider.ToLowerInvariant() switch
            {
                "sqlite" => ActivatorUtilities.CreateInstance<SqliteDataProfiler>(sp),
                "postgres" or "postgresql" => ActivatorUtilities.CreateInstance<PostgreSqlDataProfiler>(sp),
                _ => throw new NotSupportedException(
                    $"No data profiler configured for provider '{options.Provider}'. " +
                    "Supported providers: 'sqlite', 'postgres'.")
            };
        });

        // In-memory repositories (default; swap for EF Core adapters in production)
        services.AddSingleton<IAccountRepository, InMemoryAccountRepository>();
        services.AddSingleton<IUserRepository, InMemoryUserRepository>();
        services.AddSingleton<IAuditLogRepository, InMemoryAuditLogRepository>();
        services.AddSingleton<IRunRepository, InMemoryRunRepository>();
        services.AddSingleton<ISessionRepository, InMemorySessionRepository>();
        services.AddSingleton<IApiKeyStore, InMemoryApiKeyStore>();
        services.AddSingleton<ISecretProvider, EnvSecretProvider>();
        services.AddSingleton<IWebhookRepository, InMemoryWebhookRepository>();
        services.AddSingleton<ISchemaSnapshotRepository, InMemorySchemaSnapshotRepository>();
        services.AddSingleton<IProfileSnapshotRepository, InMemoryProfileSnapshotRepository>();

        // #65 — the remaining platform aggregates. Singletons here because the in-memory adapters *are* the store;
        // AddFabricatePersistence replaces every one with a scoped EF adapter.
        services.AddSingleton<IWorkspaceRepository, InMemoryWorkspaceRepository>();
        services.AddSingleton<IConnectionRepository, InMemoryConnectionRepository>();
        services.AddSingleton<IInstructionVersionRepository, InMemoryInstructionVersionRepository>();
        services.AddSingleton<IProjectDatabaseRepository, InMemoryProjectDatabaseRepository>();
        services.AddSingleton<IWorkflowRepository, InMemoryWorkflowRepository>();
        services.AddSingleton<ISkillRepository, InMemorySkillRepository>();
        services.AddSingleton<IAccountGroupRepository, InMemoryAccountGroupRepository>();
        services.AddSingleton<IAllowedDomainRepository, InMemoryAllowedDomainRepository>();

        // HTTP delivery for webhooks
        services.AddHttpClient("webhook", c => c.Timeout = TimeSpan.FromSeconds(10));
        // Consumes IWebhookRepository, which is scoped once persistence is enabled (#78).
        services.AddScoped<IWebhookDeliveryService, HttpWebhookDeliveryService>();

        // #52 — NoSQL schema discoverer stubs (full implementations tracked in issues #53–#56)
        services.AddSingleton<INoSqlSchemaDiscoverer, CosmosDbSchemaDiscoverer>();
        services.AddSingleton<INoSqlSchemaDiscoverer, MongoDbSchemaDiscoverer>();
        services.AddSingleton<INoSqlSchemaDiscoverer, DynamoDbSchemaDiscoverer>();
        services.AddSingleton<INoSqlSchemaDiscoverer, FirestoreSchemaDiscoverer>();
        services.AddSingleton<INoSqlSchemaDiscovererFactory, NoSqlSchemaDiscovererFactory>();

        return services;
    }

    /// <summary>
    /// LLM provider access and bring-your-own-key credentials (#46/#47/#58/#60). Fails fast on a misconfigured
    /// platform credential; an unset <c>FABRICATE_LLM_PROVIDER</c> simply disables the platform credential.
    /// </summary>
    public static IServiceCollection AddFabricateLlm(this IServiceCollection services, LlmOptions options, string? dataProtectionKeyPath = null)
    {
        var errors = options.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("LLM configuration is invalid:\n - " + string.Join("\n - ", errors));
        }

        services.AddSingleton(options);

        // Tenant secrets are encrypted at rest with Data Protection. The key ring must live outside the
        // application database so a database dump alone cannot decrypt credentials.
        var dp = services.AddDataProtection().SetApplicationName("Fabricate");
        if (!string.IsNullOrWhiteSpace(dataProtectionKeyPath))
        {
            dp.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath));
        }

        services.AddHttpClient(ChatCompletionClientFactory.HttpClientName);

        services.AddSingleton<ISecretCipher, DataProtectionSecretCipher>();
        services.AddSingleton<ILlmCredentialStore, InMemoryLlmCredentialStore>();

        // #77 — usage attribution. Singleton in-memory by default; AddFabricatePersistence swaps in the EF
        // adapter. The recorder alias lets the client factory depend on the write half alone.
        services.TryAddSingleton<ILlmUsageRepository, InMemoryLlmUsageRepository>();
        services.TryAddSingleton<ILlmUsageRecorder, ScopedLlmUsageRecorder>();
        services.TryAddScoped<ILlmUsageService, LlmUsageService>();

        // #83 — the prompt data boundary is stateless policy. Registered here as well as in the core
        // registration because LlmCredentialService, which enforces the opt-in refusal, is registered here.
        services.TryAddSingleton<IPromptDataBoundary, PromptDataBoundary>();
        services.AddSingleton<IChatCompletionClientFactory, ChatCompletionClientFactory>();
        services.AddScoped<ILlmCredentialProbe, ChatCompletionCredentialProbe>();
        services.AddScoped<ILlmCredentialResolver, LlmCredentialResolver>();
        services.AddScoped<ILlmCredentialService, LlmCredentialService>();
        services.AddSingleton<ITokenBudgetEstimator, HeuristicTokenBudgetEstimator>();

        return services;
    }
}
