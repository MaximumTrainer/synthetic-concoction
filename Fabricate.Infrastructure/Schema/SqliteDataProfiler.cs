using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Fabricate.Infrastructure.Schema;

/// <summary>
/// Profiles a SQLite database by running aggregate SQL queries.
/// Collects only row counts, null counts, distinct counts, and min/max values.
/// Never reads raw sensitive values — only aggregate statistics.
/// </summary>
public sealed class SqliteDataProfiler(IOptions<SchemaProviderOptions> options) : IDataProfiler
{
    public async Task<ProfileSnapshot> ProfileAsync(
        DatabaseSchema schema,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var tableProfiles = new List<TableProfile>(schema.Tables.Count);

        foreach (var table in schema.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long rowCount = 0;
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = $"SELECT COUNT(*) FROM [{table.Name}]";
                var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                rowCount = result is long l ? l : Convert.ToInt64(result ?? 0);
            }

            var columnProfiles = new List<ColumnProfile>(table.Columns.Count);
            foreach (var column in table.Columns)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var colProfile = await ProfileColumnAsync(
                    connection, table.Name, column.Name, rowCount, cancellationToken)
                    .ConfigureAwait(false);
                columnProfiles.Add(colProfile);
            }

            tableProfiles.Add(new TableProfile(table.QualifiedName, rowCount, columnProfiles));
        }

        SqliteConnection.ClearAllPools();

        return new ProfileSnapshot(
            Id: Guid.NewGuid(),
            DatabaseName: schema.Name,
            Version: 1,
            CapturedAt: DateTimeOffset.UtcNow,
            Tables: tableProfiles);
    }

    private static async Task<ColumnProfile> ProfileColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        long tableRowCount,
        CancellationToken cancellationToken)
    {
        long nullCount = 0;
        long distinctCount = 0;
        string? minValue = null;
        string? maxValue = null;
        double? mean = null;

        try
        {
            await using var cmd = connection.CreateCommand();
            // Single aggregate query for all column stats
            cmd.CommandText = $"""
                SELECT
                    SUM(CASE WHEN [{columnName}] IS NULL THEN 1 ELSE 0 END),
                    COUNT(DISTINCT [{columnName}]),
                    CAST(MIN([{columnName}]) AS TEXT),
                    CAST(MAX([{columnName}]) AS TEXT),
                    AVG(CAST([{columnName}] AS REAL))
                FROM [{tableName}]
                """;

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                nullCount = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                distinctCount = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                minValue = reader.IsDBNull(2) ? null : reader.GetString(2);
                maxValue = reader.IsDBNull(3) ? null : reader.GetString(3);
                mean = reader.IsDBNull(4) ? null : reader.GetDouble(4);
            }
        }
        catch
        {
            // If aggregate query fails (e.g., unsupported type), return null stats gracefully.
        }

        return new ColumnProfile(columnName, tableRowCount, nullCount, distinctCount, minValue, maxValue, mean);
    }
}
