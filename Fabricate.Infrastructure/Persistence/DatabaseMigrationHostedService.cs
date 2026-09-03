using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fabricate.Infrastructure.Persistence;

/// <summary>
/// Applies pending EF Core migrations before the application starts serving. A fresh database therefore gets its
/// schema on first boot with no manual step. EF Core 9 takes a database-level migration lock inside
/// <c>MigrateAsync</c>, so several API instances starting at once (a scale-out deploy) apply the schema exactly once
/// and the rest wait. Hosted services start in registration order: register this before anything that seeds data.
/// </summary>
public sealed class DatabaseMigrationHostedService(IServiceScopeFactory scopeFactory, ILogger<DatabaseMigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FabricateDbContext>();

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        if (pending.Count == 0)
        {
            logger.LogInformation("Database schema is current ({Provider}).", db.Database.ProviderName);
            return;
        }

        logger.LogInformation("Applying {Count} pending migration(s) on {Provider}: {Migrations}",
            pending.Count, db.Database.ProviderName, string.Join(", ", pending));

        await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Database migrations applied.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
