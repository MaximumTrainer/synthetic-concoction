using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fabricate.Infrastructure.Schema;

/// <summary>
/// Profiles a PostgreSQL database using aggregate SQL queries only.
/// Collects row counts, null counts, distinct counts, and min/max values.
/// Never reads raw row data — only aggregate statistics.
/// </summary>
public sealed class PostgreSqlDataProfiler(IOptions<SchemaProviderOptions> options) : IDataProfiler
{
    public async Task<ProfileSnapshot> ProfileAsync(
        DatabaseSchema schema,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var tableProfiles = new List<TableProfile>(schema.Tables.Count);

        foreach (var table in schema.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Qualify with schema prefix (qualifiedName = "schema.table")
            var qualifiedRef = $"\"{table.Schema}\".\"{table.Name}\"";

            long rowCount = 0;
            await using (var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM {qualifiedRef}", connection))
            {
                var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                rowCount = result is long l ? l : Convert.ToInt64(result ?? 0);
            }

            var columnProfiles = new List<ColumnProfile>(table.Columns.Count);
            foreach (var column in table.Columns)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var colProfile = await ProfileColumnAsync(
                    connection, qualifiedRef, column.Name, rowCount, cancellationToken)
                    .ConfigureAwait(false);
                columnProfiles.Add(colProfile);
            }

            tableProfiles.Add(new TableProfile(table.QualifiedName, rowCount, columnProfiles));
        }

        return new ProfileSnapshot(
            Id: Guid.NewGuid(),
            DatabaseName: schema.Name,
            Version: 1,
            CapturedAt: DateTimeOffset.UtcNow,
            Tables: tableProfiles);
    }

    private static async Task<ColumnProfile> ProfileColumnAsync(
        NpgsqlConnection connection,
        string qualifiedTableRef,
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
            var quotedColumn = $"\"{columnName}\"";
            var sql = $"""
                SELECT
                    SUM(CASE WHEN {quotedColumn} IS NULL THEN 1 ELSE 0 END),
                    COUNT(DISTINCT {quotedColumn}),
                    CAST(MIN({quotedColumn}) AS TEXT),
                    CAST(MAX({quotedColumn}) AS TEXT),
                    AVG({quotedColumn}::numeric)
                FROM {qualifiedTableRef}
                """;

            await using var cmd = new NpgsqlCommand(sql, connection);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                nullCount = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                distinctCount = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                minValue = reader.IsDBNull(2) ? null : reader.GetString(2);
                maxValue = reader.IsDBNull(3) ? null : reader.GetString(3);
                mean = reader.IsDBNull(4) ? null : (double?)reader.GetDouble(4);
            }
        }
        catch
        {
            // Gracefully skip aggregate stats for columns with unsupported types (e.g., JSON, arrays).
        }

        return new ColumnProfile(columnName, tableRowCount, nullCount, distinctCount, minValue, maxValue, mean);
    }
}
