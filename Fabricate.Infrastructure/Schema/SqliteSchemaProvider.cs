using Fabricate.Application.Abstractions;
using Fabricate.Application.Schema;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Fabricate.Infrastructure.Schema;

public sealed class SqliteSchemaProvider(IOptions<SchemaProviderOptions> options) : ISchemaProvider
{
    public string ProviderName => "sqlite";

    public async Task<DatabaseSchema> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var tables = new List<TableSchema>();
            await using var connection = new SqliteConnection(options.Value.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            const string sql = "SELECT name FROM sqlite_master WHERE type = $type AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            await using var tableCmd = new SqliteCommand(sql, connection);
            tableCmd.Parameters.AddWithValue("$type", "table");

            await using var reader = await tableCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var tableName = reader.GetString(0);
                tables.Add(await DiscoverTableAsync(connection, tableName, cancellationToken).ConfigureAwait(false));
            }

            return new DatabaseSchema(options.Value.DatabaseName, tables);
        }
        catch (Exception ex)
        {
            throw new SchemaProviderException(ProviderName, "Failed to discover SQLite schema.", ex);
        }
    }

    private static async Task<TableSchema> DiscoverTableAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        var columns = new List<ColumnSchema>();
        var primaryKey = new List<string>();
        var uniqueConstraints = new List<UniqueConstraintSchema>();
        var indexes = new List<IndexSchema>();

        await using (var pragmaColumns = connection.CreateCommand())
        {
            pragmaColumns.CommandText = $"PRAGMA table_info(\"{tableName}\");";
            await using var columnReader = await pragmaColumns.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await columnReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var columnName = columnReader.GetString(1);
                var sqlType = columnReader.IsDBNull(2) ? "text" : columnReader.GetString(2);
                var isNullable = columnReader.GetInt32(3) == 0;
                var defaultValue = columnReader.IsDBNull(4) ? null : columnReader.GetString(4);
                var isPk = columnReader.GetInt32(5) > 0;

                if (isPk)
                {
                    primaryKey.Add(columnName);
                }

                columns.Add(new ColumnSchema(
                    columnName,
                    sqlType,
                    SqlTypeMapper.MapToDataKind(sqlType),
                    isNullable,
                    isPk,
                    false,
                    null,
                    null,
                    null,
                    defaultValue,
                    AllowedValues: null));
            }
        }

        await using (var pragmaIndex = connection.CreateCommand())
        {
            pragmaIndex.CommandText = $"PRAGMA index_list(\"{tableName}\");";
            await using var indexReader = await pragmaIndex.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await indexReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var indexName = indexReader.GetString(1);
                var isUnique = indexReader.GetInt32(2) == 1;

                var indexColumns = new List<string>();
                await using var idxInfo = connection.CreateCommand();
                idxInfo.CommandText = $"PRAGMA index_info(\"{indexName}\");";
                await using var idxReader = await idxInfo.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await idxReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    indexColumns.Add(idxReader.GetString(2));
                }

                indexes.Add(new IndexSchema(indexName, indexColumns, isUnique));
                if (isUnique)
                {
                    uniqueConstraints.Add(new UniqueConstraintSchema(indexName, indexColumns));
                }
            }
        }

        var foreignKeys = await DiscoverForeignKeysAsync(connection, tableName, cancellationToken).ConfigureAwait(false);

        // Only mark IsUnique for columns that are the sole column in a single-column unique constraint.
        // Composite unique constraints are represented in UniqueConstraints and must not set IsUnique on individual columns.
        var singleColumnUniqueConstraintColumns = uniqueConstraints
            .Where(static u => u.Columns.Count == 1)
            .Select(static u => u.Columns[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var finalizedColumns = columns
            .Select(c => c with { IsUnique = singleColumnUniqueConstraintColumns.Contains(c.Name) })
            .ToArray();

        return new TableSchema("main", tableName, finalizedColumns, primaryKey, foreignKeys, uniqueConstraints, indexes);
    }

    private static async Task<IReadOnlyList<ForeignKeySchema>> DiscoverForeignKeysAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        var items = new List<(string ReferencedTable, string SourceColumn, string ReferencedColumn, int Id)>();

        await using var fkCmd = connection.CreateCommand();
        fkCmd.CommandText = $"PRAGMA foreign_key_list(\"{tableName}\");";
        await using var fkReader = await fkCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await fkReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add((
                fkReader.GetString(2),
                fkReader.GetString(3),
                fkReader.GetString(4),
                fkReader.GetInt32(0)));
        }

        return items
            .GroupBy(static x => x.Id)
            .Select(group => new ForeignKeySchema(
                $"fk_{tableName}_{group.Key}",
                $"main.{tableName}",
                group.Select(static x => x.SourceColumn).ToArray(),
                $"main.{group.First().ReferencedTable}",
                group.Select(static x => x.ReferencedColumn).ToArray()))
            .ToArray();
    }
}
