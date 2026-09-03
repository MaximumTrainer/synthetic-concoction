using Fabricate.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fabricate.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>SQLite persistence. Kept for local development and tests; hosted deployments use <c>postgres</c>.</summary>
    public static IServiceCollection AddFabricatePersistence(this IServiceCollection services, string connectionString)
        => services.AddFabricatePersistence("sqlite", connectionString);

    /// <summary>
    /// Durable persistence behind the repository ports. <paramref name="provider"/> is <c>sqlite</c> or <c>postgres</c>.
    /// Pending EF migrations are applied at startup by <see cref="DatabaseMigrationHostedService"/>, which must run
    /// before any other hosted service that touches the database — register this before them.
    /// </summary>
    public static IServiceCollection AddFabricatePersistence(this IServiceCollection services, string provider, string connectionString)
    {
        switch (provider.Trim().ToLowerInvariant())
        {
            case "sqlite":
                services.AddDbContext<FabricateDbContext>(options => options.UseSqlite(connectionString));
                break;

            case "postgres" or "postgresql":
                // Repositories depend on the base type; resolve it to the PostgreSQL subclass that owns the Npgsql migrations.
                services.AddDbContext<FabricatePostgresDbContext>(options => options.UseNpgsql(connectionString));
                services.AddScoped<FabricateDbContext>(sp => sp.GetRequiredService<FabricatePostgresDbContext>());
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported database provider '{provider}'. FABRICATE_DB_PROVIDER must be 'sqlite' or 'postgres' (or unset for in-memory).");
        }

        services.AddHostedService<DatabaseMigrationHostedService>();

        // Replace in-memory repository singletons with EF Core scoped adapters
        services.AddScoped<IAccountRepository, EfAccountRepository>();
        services.AddScoped<IUserRepository, EfUserRepository>();
        services.AddScoped<IAuditLogRepository, EfAuditLogRepository>();
        services.AddScoped<IRunRepository, EfRunRepository>();
        services.AddScoped<ISessionRepository, EfSessionRepository>();
        services.AddScoped<IApiKeyStore, EfApiKeyStore>();
        services.AddScoped<ILlmCredentialStore, EfLlmCredentialStore>();

        return services;
    }
}
