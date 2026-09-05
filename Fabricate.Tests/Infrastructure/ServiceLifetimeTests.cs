using Fabricate.Application.Abstractions;
using Fabricate.Application.Llm;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.DependencyInjection;
using Fabricate.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fabricate.Tests.Infrastructure;

/// <summary>
/// #78 regression guard. A singleton that depends on a scoped service is a captive dependency: in Production
/// (where ValidateScopes defaults to off) it resolves the scoped service once from the root and shares it — for
/// the EF adapters that means one FabricateDbContext across every concurrent request, and DbContext is not
/// thread-safe. Building the provider with validation on turns that into a startup failure, so these tests fail
/// the build rather than letting it reach a deployment.
/// </summary>
public sealed class ServiceLifetimeTests
{
    private static ServiceProvider BuildProvider(string? dbProvider, string? sqliteConnectionString = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFabricateApplication(seed: 42);
        services.AddFabricateInfrastructure(o => { o.Provider = "sqlite"; o.ConnectionString = "Data Source=:memory:"; });
        services.AddFabricateLlm(new LlmOptions());

        if (dbProvider is not null)
        {
            var connectionString = dbProvider == "postgres"
                ? "Host=localhost;Database=fabricate_validation;Username=postgres;Password=postgres"
                : sqliteConnectionString ?? "Data Source=:memory:";
            services.AddFabricatePersistence(dbProvider, connectionString);
        }

        // Exactly what the API does at startup — and what would have caught this before it shipped.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    [Theory]
    [InlineData(null)]        // in-memory repositories
    [InlineData("sqlite")]
    [InlineData("postgres")]  // registration only; no connection is opened
    public void ServiceGraph_ValidatesForEveryDatabaseProvider(string? dbProvider)
    {
        var act = () => BuildProvider(dbProvider).Dispose();

        act.Should().NotThrow("no singleton may depend on a scoped service in any supported configuration");
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void ServicesThatTouchRepositories_AreResolvableFromARequestScope(string dbProvider)
    {
        using var provider = BuildProvider(dbProvider);
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetRequiredService<IAgentChatService>().Should().NotBeNull();
        sp.GetRequiredService<IApiKeyService>().Should().NotBeNull();
        sp.GetRequiredService<IAccountService>().Should().NotBeNull();
        sp.GetRequiredService<IWorkspaceService>().Should().NotBeNull();
        sp.GetRequiredService<IWorkflowService>().Should().NotBeNull();
        sp.GetRequiredService<ILlmCredentialService>().Should().NotBeNull();
        sp.GetRequiredService<IWebhookService>().Should().NotBeNull();
    }

    [Fact]
    public void RepositoriesAreScoped_WhenPersistenceIsEnabled_SoEachRequestGetsItsOwnDbContext()
    {
        using var provider = BuildProvider("sqlite");

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        first.ServiceProvider.GetRequiredService<FabricateDbContext>()
            .Should().NotBeSameAs(second.ServiceProvider.GetRequiredService<FabricateDbContext>());
        first.ServiceProvider.GetRequiredService<ISessionRepository>()
            .Should().NotBeSameAs(second.ServiceProvider.GetRequiredService<ISessionRepository>());
    }

    [Fact]
    public void StatelessHelpers_RemainSingletons()
    {
        using var provider = BuildProvider("sqlite");

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        first.ServiceProvider.GetRequiredService<ITokenBudgetEstimator>()
            .Should().BeSameAs(second.ServiceProvider.GetRequiredService<ITokenBudgetEstimator>());
        first.ServiceProvider.GetRequiredService<IToolRegistry>()
            .Should().BeSameAs(second.ServiceProvider.GetRequiredService<IToolRegistry>(),
                "the tool registry holds the per-workspace allowlist and must not be per-request");
        first.ServiceProvider.GetRequiredService<ISyntheticDataOrchestrator>()
            .Should().BeSameAs(second.ServiceProvider.GetRequiredService<ISyntheticDataOrchestrator>());
    }

    /// <summary>
    /// The behaviour the captive dependency actually broke: concurrent requests each need their own DbContext.
    /// A file-backed SQLite database is used because every ":memory:" connection is a separate database.
    /// </summary>
    [Fact]
    public async Task ConcurrentScopedOperations_DoNotShareADbContext()
    {
        var file = Path.Combine(Path.GetTempPath(), $"fabricate-lifetime-{Guid.NewGuid():N}.db");
        using var provider = BuildProvider("sqlite", $"Data Source={file}");
        try
        {
            using (var seed = provider.CreateScope())
            {
                await seed.ServiceProvider.GetRequiredService<FabricateDbContext>().Database.EnsureCreatedAsync();
            }

            var work = Enumerable.Range(0, 16).Select(async i =>
            {
                using var scope = provider.CreateScope();
                var accounts = scope.ServiceProvider.GetRequiredService<IAccountRepository>();
                var account = new Account(Guid.NewGuid(), $"Acme {i}", DateTimeOffset.UtcNow);
                await accounts.SaveAsync(account);
                return await accounts.GetByIdAsync(account.Id);
            });

            var saved = await Task.WhenAll(work);

            saved.Should().OnlyContain(a => a != null);
            saved.Select(a => a!.Id).Distinct().Should().HaveCount(16);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
