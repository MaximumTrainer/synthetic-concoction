using Fabricate.Application.Abstractions;
using Fabricate.Application.Governance;

namespace Fabricate.Api;

/// <summary>
/// Applies the audit retention window on a timer (#74). Does nothing at all when retention is disabled, which is
/// the default, so an existing deployment keeps every event until an operator asks otherwise.
/// </summary>
public sealed class AuditRetentionService(
    IServiceProvider services,
    AuditRetentionOptions options,
    ILogger<AuditRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.IsEnabled)
        {
            logger.LogInformation("Audit retention is disabled; events are kept indefinitely.");
            return;
        }

        logger.LogInformation(
            "Audit retention enabled: keeping {RetentionDays} days, sweeping every {SweepInterval}.",
            options.RetentionDays,
            options.SweepInterval);

        using var timer = new PeriodicTimer(options.SweepInterval);
        do
        {
            await SweepAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A new scope per sweep: the audit repository is scoped when a database provider is configured.
            await using var scope = services.CreateAsyncScope();
            var auditLog = scope.ServiceProvider.GetRequiredService<IAuditLogService>();

            var deleted = await auditLog.ApplyRetentionAsync(cancellationToken).ConfigureAwait(false);
            if (deleted > 0)
            {
                logger.LogInformation("Audit retention removed {DeletedCount} events older than {RetentionDays} days.", deleted, options.RetentionDays);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // Housekeeping must never take the API down; the next tick tries again.
            logger.LogError(ex, "Audit retention sweep failed; it will be retried on the next interval.");
        }
    }
}
