using Fabricate.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fabricate.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddFabricatePersistence(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<FabricateDbContext>(options =>
            options.UseSqlite(connectionString));

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
