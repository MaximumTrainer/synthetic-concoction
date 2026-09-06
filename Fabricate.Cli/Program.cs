using System.CommandLine;
using System.Text.Json;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Configuration;
using Fabricate.Application.Llm;
using Fabricate.Infrastructure.DependencyInjection;
using Fabricate.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var root = new RootCommand("Fabricate synthetic data CLI");

var providerOption = new Option<string>("--provider", getDefaultValue: () => "sqlite",
    description: "Provider: sqlite, postgres, or a NoSQL provider — cosmosdb, mongodb, dynamodb, firestore");

/// <summary>The NoSQL providers, which take a different discovery path from the relational ones (#71).</summary>
static bool IsNoSqlProvider(string provider) =>
    provider.ToLowerInvariant() is "cosmosdb" or "mongodb" or "dynamodb" or "firestore";
var connectionOption = new Option<string>("--connection", "Database connection string") { IsRequired = true };
var dbNameOption = new Option<string>("--database", getDefaultValue: () => "fabricate", description: "Database name in synthetic model");
var seedOption = new Option<long>("--seed", getDefaultValue: () => 42L, description: "Deterministic seed");
var rulesOption = new Option<string?>("--rules", "Path to rule configuration JSON/YAML");
var outputOption = new Option<string>("--output", getDefaultValue: () => "./artifacts", description: "Output directory");
var rowsOption = new Option<int>("--rows", getDefaultValue: () => 10, description: "Default row count per table");
var complianceOption = new Option<ComplianceProfile>("--compliance-profile", getDefaultValue: () => ComplianceProfile.Default, description: "Compliance profile: Default, Healthcare (HIPAA), Finance (PCI)");

var discover = new Command("discover", "Discover schema")
{
    providerOption, connectionOption, dbNameOption, seedOption
};

discover.SetHandler(async (provider, connection, database, seed) =>
{
    // A NoSQL provider is discovered through its own adapter: there is no relational schema to build, and the
    // metadata model has no foreign-key graph because document stores do not declare one.
    if (IsNoSqlProvider(provider))
    {
        using var noSqlHost = BuildHost("sqlite", connection, database, seed);
        var discoverer = noSqlHost.Services.GetRequiredService<INoSqlSchemaDiscovererFactory>().GetDiscoverer(provider);
        var collections = await discoverer.DiscoverCollectionsAsync(connection, database);

        Console.WriteLine(JsonSerializer.Serialize(collections, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        return;
    }

    using var host = BuildHost(provider, connection, database, seed);
    var orchestrator = host.Services.GetRequiredService<ISyntheticDataOrchestrator>();
    var schema = await orchestrator.DiscoverAsync();
    Console.WriteLine(JsonSerializer.Serialize(schema, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
}, providerOption, connectionOption, dbNameOption, seedOption);

var generate = new Command("generate", "Discover + generate + export")
{
    providerOption, connectionOption, dbNameOption, seedOption, rulesOption, outputOption, rowsOption, complianceOption
};

var streamingOption = new Option<bool>("--streaming", getDefaultValue: () => false, description: "Use streaming generation path (low memory, suitable for large datasets)");
generate.AddOption(streamingOption);

generate.SetHandler(async ctx =>
{
    var provider = ctx.ParseResult.GetValueForOption(providerOption)!;
    var connection = ctx.ParseResult.GetValueForOption(connectionOption);
    var database = ctx.ParseResult.GetValueForOption(dbNameOption);
    var seed = ctx.ParseResult.GetValueForOption(seedOption);
    var rules = ctx.ParseResult.GetValueForOption(rulesOption);
    var output = ctx.ParseResult.GetValueForOption(outputOption)!;
    var rows = ctx.ParseResult.GetValueForOption(rowsOption);
    var compliance = ctx.ParseResult.GetValueForOption(complianceOption);
    var streaming = ctx.ParseResult.GetValueForOption(streamingOption);

    using var host = BuildHost(provider, connection ?? string.Empty, database ?? string.Empty, seed);
    var services = host.Services;
    var orchestrator = services.GetRequiredService<ISyntheticDataOrchestrator>();
    var ruleService = services.GetRequiredService<IRuleConfigurationService>();
    var exporters = services.GetServices<IExporter>().ToArray();

    var schema = await orchestrator.DiscoverAsync();
    var rowCounts = schema.Tables.ToDictionary(static t => t.QualifiedName, _ => rows, StringComparer.Ordinal);

    RuleConfiguration? loadedRules = null;
    if (!string.IsNullOrWhiteSpace(rules))
    {
        loadedRules = ruleService.Load(rules!);
        var validationErrors = ruleService.Validate(loadedRules);
        if (validationErrors.Count > 0)
        {
            foreach (var err in validationErrors)
            {
                Console.Error.WriteLine(err);
            }

            Environment.ExitCode = 1;
            return;
        }

        // Apply precedence merge: defaults < schema-derived < user rules.
        var emptyConfig = new RuleConfiguration { Version = loadedRules.Version, Tables = [] };
        loadedRules = ruleService.Merge(emptyConfig, emptyConfig, loadedRules);
    }

    var request = new GenerationRequest(schema, rowCounts, seed, loadedRules, compliance);

    if (streaming)
    {
        // Streaming path: write rows to disk incrementally, only PK keys buffered in memory.
        var streamingExporters = exporters.OfType<IStreamingExporter>().ToArray();
        if (streamingExporters.Length == 0)
        {
            Console.Error.WriteLine("No streaming-capable exporters registered. Use a format that supports streaming (csv or json).");
            Environment.ExitCode = 1;
            return;
        }

        foreach (var exporter in streamingExporters)
        {
            var summary = await orchestrator.GenerateStreamingAsync(request, exporter, Path.Combine(output, exporter.Name));
            var summaryPath = Path.Combine(output, $"summary-{exporter.Name}.json");
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            Console.WriteLine($"[{exporter.Name}] Generated {summary.RowCount} rows across {summary.TableCount} tables (streaming).");
        }
    }
    else
    {
        var (result, summary) = await orchestrator.GenerateAsync(request);

        foreach (var exporter in exporters)
        {
            await exporter.ExportAsync(result.Tables, Path.Combine(output, exporter.Name));
        }

        var summaryPath = Path.Combine(output, "summary.json");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        Console.WriteLine($"Generated {summary.RowCount} rows across {summary.TableCount} tables.");

        if (!result.IsSuccess)
        {
            Console.Error.WriteLine($"Validation issues: {result.ValidationIssues.Count}");
            Environment.ExitCode = 2;
        }
    }
});

var validate = new Command("validate", "Generate and return validation summary")
{
    providerOption, connectionOption, dbNameOption, seedOption, rowsOption
};

validate.SetHandler(async (provider, connection, database, seed, rows) =>
{
    using var host = BuildHost(provider, connection, database, seed);
    var orchestrator = host.Services.GetRequiredService<ISyntheticDataOrchestrator>();
    var schema = await orchestrator.DiscoverAsync();
    var rowCounts = schema.Tables.ToDictionary(static t => t.QualifiedName, _ => rows, StringComparer.Ordinal);

    var (result, summary) = await orchestrator.GenerateAsync(new GenerationRequest(schema, rowCounts, seed));
    Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

    if (!result.IsSuccess)
    {
        foreach (var issue in result.ValidationIssues)
        {
            Console.Error.WriteLine($"{issue.Table}.{issue.Column}: {issue.Reason}");
        }

        Environment.ExitCode = 3;
    }
}, providerOption, connectionOption, dbNameOption, seedOption, rowsOption);

var export = new Command("export", "Generate and export using chosen format")
{
    providerOption, connectionOption, dbNameOption, seedOption, outputOption, rowsOption
};

var formatOption = new Option<string>("--format", getDefaultValue: () => "json", description: "Export format: json, csv, sql, or parquet");
export.AddOption(formatOption);

export.SetHandler(async (provider, connection, database, seed, output, rows, format) =>
{
    using var host = BuildHost(provider, connection, database, seed);
    var orchestrator = host.Services.GetRequiredService<ISyntheticDataOrchestrator>();
    var exporters = host.Services.GetServices<IExporter>().Where(e => string.Equals(e.Name, format, StringComparison.OrdinalIgnoreCase)).ToArray();

    if (exporters.Length == 0)
    {
        Console.Error.WriteLine($"Unsupported exporter '{format}'.");
        Environment.ExitCode = 1;
        return;
    }

    var schema = await orchestrator.DiscoverAsync();
    var rowCounts = schema.Tables.ToDictionary(static t => t.QualifiedName, _ => rows, StringComparer.Ordinal);
    var (result, _) = await orchestrator.GenerateAsync(new GenerationRequest(schema, rowCounts, seed));

    foreach (var exporter in exporters)
    {
        await exporter.ExportAsync(result.Tables, output);
    }

    if (!result.IsSuccess)
    {
        Environment.ExitCode = 2;
    }
}, providerOption, connectionOption, dbNameOption, seedOption, outputOption, rowsOption, formatOption);

var discoverProfile = new Command("discover-profile", "Profile schema data distribution and surface diagnostics")
{
    providerOption, connectionOption, dbNameOption, seedOption
};

discoverProfile.SetHandler(async (provider, connection, database, seed) =>
{
    if (IsNoSqlProvider(provider))
    {
        using var noSqlHost = BuildHost("sqlite", connection, database, seed);
        var discoverer = noSqlHost.Services.GetRequiredService<INoSqlSchemaDiscovererFactory>().GetDiscoverer(provider);
        var profiler = noSqlHost.Services.GetRequiredService<INoSqlDataProfilerFactory>().GetProfiler(provider);

        var collections = await discoverer.DiscoverCollectionsAsync(connection, database);
        var snapshot = await profiler.ProfileAsync(collections, connection);

        Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        return;
    }

    using var host = BuildHost(provider, connection, database, seed);
    var orchestrator = host.Services.GetRequiredService<ISyntheticDataOrchestrator>();
    var reviewService = host.Services.GetRequiredService<ISchemaReviewService>();
    var schema = await orchestrator.DiscoverAsync();
    var diagnostics = reviewService.Review(schema);

    Console.WriteLine(JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

    if (diagnostics.Diagnostics.Count > 0)
    {
        Console.Error.WriteLine($"{diagnostics.Diagnostics.Count} diagnostic(s) found. See output for details.");
        Environment.ExitCode = 1;
    }
}, providerOption, connectionOption, dbNameOption, seedOption);

// #76 — re-protects an existing Data Protection key ring under whatever protection is configured now. Needed
// because Data Protection encrypts on write and never revisits stored entries: an operator who enables a
// key-encryption key protects only newly created ring entries, and the existing ones stay in the clear while
// the configuration says otherwise.
var secrets = new Command("secrets", "Operator commands for stored secrets");

var rewrap = new Command("rewrap",
    "Re-protect the Data Protection key ring under the currently configured key. Idempotent.");

rewrap.SetHandler(async () =>
{
    var dbProvider = Environment.GetEnvironmentVariable("FABRICATE_DB_PROVIDER") ?? "memory";
    var connection = Environment.GetEnvironmentVariable("FABRICATE_CONNECTION_STRING");

    if (dbProvider.Equals("memory", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(connection))
    {
        Console.Error.WriteLine(
            "Rewrapping needs the database the key ring lives in. Set FABRICATE_DB_PROVIDER and " +
            "FABRICATE_CONNECTION_STRING to the same values the API runs with.");
        Environment.ExitCode = 1;
        return;
    }

    var services = new ServiceCollection();
    services.AddLogging();
    services.AddFabricatePersistence(dbProvider, connection);
    services.AddFabricateLlm(LlmOptions.FromEnvironment(Environment.GetEnvironmentVariable),
        KeyRingOptions.FromEnvironment(Environment.GetEnvironmentVariable));

    await using var provider = services.BuildServiceProvider();

    var report = await provider.GetRequiredService<KeyRingRewrapService>().RewrapAsync();
    Console.WriteLine(report);
});

secrets.AddCommand(rewrap);
root.AddCommand(secrets);

root.AddCommand(discover);
root.AddCommand(discoverProfile);
root.AddCommand(generate);
root.AddCommand(validate);
root.AddCommand(export);

return await root.InvokeAsync(args);

static IHost BuildHost(string provider, string connectionString, string database, long seed)
{
    return Host.CreateDefaultBuilder()
        .ConfigureServices(services =>
        {
            services.AddFabricateApplication(seed);
            services.AddFabricateInfrastructure(options =>
            {
                options.Provider = provider;
                options.ConnectionString = connectionString;
                options.DatabaseName = database;
            });
        })
        .Build();
}
