using System.Globalization;

namespace Fabricate.Application.Governance;

/// <summary>
/// How long audit events are kept (#74). Audit rows are insert-only and otherwise grow without bound, which on a
/// hosted deployment eventually costs more than the log is worth.
/// </summary>
public sealed record AuditRetentionOptions
{
    /// <summary>Days to keep. <c>0</c> — the default — keeps everything, so upgrading changes no behaviour.</summary>
    public int RetentionDays { get; init; }

    /// <summary>How often the purge runs. Retention is a housekeeping window, not a deadline.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Rows per DELETE, so clearing a long backlog does not hold one long write lock.</summary>
    public int BatchSize { get; init; } = 1_000;

    public bool IsEnabled => RetentionDays > 0;

    /// <summary>The instant before which events are eligible for deletion.</summary>
    public DateTimeOffset CutoffFrom(DateTimeOffset now) => now.AddDays(-RetentionDays);

    /// <summary>
    /// Reads <c>FABRICATE_AUDIT_RETENTION_DAYS</c>, <c>FABRICATE_AUDIT_SWEEP_MINUTES</c> and
    /// <c>FABRICATE_AUDIT_PURGE_BATCH_SIZE</c>. Unparseable or non-positive values fall back to the defaults
    /// rather than throwing: a typo in an environment variable must not stop the API from starting.
    /// </summary>
    public static AuditRetentionOptions FromEnvironment(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var defaults = new AuditRetentionOptions();

        return new AuditRetentionOptions
        {
            RetentionDays = PositiveInt(read("FABRICATE_AUDIT_RETENTION_DAYS")) ?? 0,
            SweepInterval = PositiveInt(read("FABRICATE_AUDIT_SWEEP_MINUTES")) is int minutes
                ? TimeSpan.FromMinutes(minutes)
                : defaults.SweepInterval,
            BatchSize = PositiveInt(read("FABRICATE_AUDIT_PURGE_BATCH_SIZE")) ?? defaults.BatchSize,
        };
    }

    private static int? PositiveInt(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;
}
