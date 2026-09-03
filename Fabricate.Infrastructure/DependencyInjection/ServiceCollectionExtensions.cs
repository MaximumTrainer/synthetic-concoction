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
using Fabricate.Application.Orchestration;
using Fabricate.Application.Planning;
using Fabricate.Application.Projects;
using Fabricate.Application.Schema;
using Fabricate.Application.Webhooks;
using Fabricate.Application.Workflows;
using Fabricate.Application.Workspaces;
using Fabricate.Infrastructure.Configuration;
using Fabricate.Infrastructure.Export;
using Fabricate.Infrastructure.Repositories;
using Fabricate.Infrastructure.Schema;
using Fabricate.Infrastructure.Webhooks;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddSingleton<RunLifecycleService>();

        // #26 — accounts
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<IInvitationService, InvitationService>();
        services.AddSingleton<IUserProfileService, UserProfileService>();

        // #27 — governance
        services.AddSingleton<IAccountGroupService, AccountGroupService>();
        services.AddSingleton<IAllowedDomainService, AllowedDomainService>();
        services.AddSingleton<IAuditLogService, AuditLogService>();

        // #28 — workspaces
        services.AddSingleton<IWorkspaceService, WorkspaceService>();
        services.AddSingleton<IConnectionCatalogService, ConnectionCatalogService>();
        services.AddSingleton<IInstructionVersionService, InstructionVersionService>();

        // #29 — projects
        services.AddSingleton<IProjectRepository, InMemoryProjectRepository>();
        services.AddSingleton<IProjectService, ProjectService>();
        services.AddSingleton<IProjectDatabaseCatalog, ProjectDatabaseCatalog>();

        // #30 — chat
        services.AddSingleton<IToolRegistry>(sp =>
        {
            var registry = new ToolRegistry();
            // Built-in tools registered at composition time
            registry.Register(new DiscoverSchemaTool(sp.GetRequiredService<ISchemaDiscoveryService>()));
            registry.Register(new GenerateDataTool(sp.GetRequiredService<ISyntheticDataOrchestrator>()));
            return registry;
        });
        services.AddSingleton<IAgentChatService, AgentChatService>();

        // #31 — API keys
        services.AddSingleton<IApiKeyService, ApiKeyService>();

        // #24 — workflows
        services.AddSingleton<IWorkflowService, WorkflowService>();
        services.AddSingleton<ISkillRegistry, SkillRegistryService>();
        services.AddSingleton<IApiContractIngestionService, OpenApiContractIngestionService>();

        // #13 — schema/profile snapshots
        services.AddSingleton<ISchemaSnapshotService, SchemaSnapshotService>();
        services.AddSingleton<IProfileSnapshotService, ProfileSnapshotService>();

        // #43 — webhooks
        services.AddSingleton<IWebhookService, WebhookService>();

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

        services.AddSingleton<IArtifactStore>(sp =>
        {
            var baseDir = Path.Combine(Path.GetTempPath(), "fabricate-artifacts");
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

        // HTTP delivery for webhooks
        services.AddHttpClient("webhook", c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IWebhookDeliveryService, HttpWebhookDeliveryService>();

        // #52 — NoSQL schema discoverer stubs (full implementations tracked in issues #53–#56)
        services.AddSingleton<INoSqlSchemaDiscoverer, CosmosDbSchemaDiscoverer>();
        services.AddSingleton<INoSqlSchemaDiscoverer, MongoDbSchemaDiscoverer>();
        services.AddSingleton<INoSqlSchemaDiscoverer, DynamoDbSchemaDiscoverer>();
        services.AddSingleton<INoSqlSchemaDiscoverer, FirestoreSchemaDiscoverer>();
        services.AddSingleton<INoSqlSchemaDiscovererFactory, NoSqlSchemaDiscovererFactory>();

        return services;
    }
}
