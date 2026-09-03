using Fabricate.Application.Abstractions;
using Fabricate.Application.Schema;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fabricate.Infrastructure.Schema;

public sealed class PostgreSqlSchemaProvider(IOptions<SchemaProviderOptions> options) : ISchemaProvider
{
    public string ProviderName => "postgres";

    public async Task<DatabaseSchema> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(options.Value.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var columns = await DiscoverColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
            var foreignKeys = await DiscoverForeignKeysAsync(connection, cancellationToken).ConfigureAwait(false);
            var uniques = await DiscoverUniqueConstraintsAsync(connection, cancellationToken).ConfigureAwait(false);

            var tables = columns
                .GroupBy(static c => (c.Schema, c.Table))
                .Select(group =>
                {
                    var tableName = group.Key.Table;
                    var schemaName = group.Key.Schema;
                    var qualifiedName = $"{schemaName}.{tableName}";

                    var tableUniques = uniques.Where(u => string.Equals(u.TableName, qualifiedName, StringComparison.Ordinal)).ToArray();
                    var tableForeignKeys = foreignKeys.Where(f => string.Equals(f.SourceTable, qualifiedName, StringComparison.Ordinal)).ToArray();

                    var columnSchemas = group
                        .Select(c => new ColumnSchema(
                            c.Column,
                            c.SqlType,
                            SqlTypeMapper.MapToDataKind(c.SqlType),
                            c.IsNullable,
                            c.IsPrimaryKey,
                            tableUniques.Any(u => u.Columns.Count == 1 && u.Columns.Contains(c.Column, StringComparer.OrdinalIgnoreCase)),
                            c.MaxLength,
                            c.Precision,
                            c.Scale,
                            c.DefaultValue,
                            null))
                        .ToArray();

                    var primaryKey = columnSchemas.Where(static c => c.IsPrimaryKey).Select(static c => c.Name).ToArray();

                    return new TableSchema(
                        schemaName,
                        tableName,
                        columnSchemas,
                        primaryKey,
                        tableForeignKeys,
                        tableUniques.Select(static u => new UniqueConstraintSchema(u.Name, u.Columns)).ToArray(),
                        []);
                })
                .OrderBy(static t => t.QualifiedName, StringComparer.Ordinal)
                .ToArray();

            return new DatabaseSchema(options.Value.DatabaseName, tables);
        }
        catch (Exception ex)
        {
            throw new SchemaProviderException(ProviderName, "Failed to discover PostgreSQL schema.", ex);
        }
    }

    private static async Task<IReadOnlyList<(string Schema, string Table, string Column, string SqlType, bool IsNullable, bool IsPrimaryKey, int? MaxLength, int? Precision, int? Scale, string? DefaultValue)>> DiscoverColumnsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT c.table_schema,
                                  c.table_name,
                                  c.column_name,
                                  c.data_type,
                                  c.is_nullable = 'YES' AS is_nullable,
                                  EXISTS (
                                      SELECT 1
                                      FROM information_schema.table_constraints tc
                                      JOIN information_schema.key_column_usage kcu
                                        ON tc.constraint_name = kcu.constraint_name
                                       AND tc.table_schema = kcu.table_schema
                                     WHERE tc.constraint_type = 'PRIMARY KEY'
                                       AND tc.table_schema = c.table_schema
                                       AND tc.table_name = c.table_name
                                       AND kcu.column_name = c.column_name) AS is_primary_key,
                                  c.character_maximum_length,
                                  c.numeric_precision,
                                  c.numeric_scale,
                                  c.column_default
                           FROM information_schema.columns c
                           WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema')
                           ORDER BY c.table_schema, c.table_name, c.ordinal_position;
                           """;

        var result = new List<(string, string, string, string, bool, bool, int?, int?, int?, string?)>();
        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return result;
    }

    private static async Task<IReadOnlyList<ForeignKeySchema>> DiscoverForeignKeysAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT tc.constraint_name,
                                  tc.table_schema,
                                  tc.table_name,
                                  ccu.table_schema AS foreign_table_schema,
                                  ccu.table_name AS foreign_table_name,
                                  kcu.column_name,
                                  ccu.column_name AS foreign_column_name
                           FROM information_schema.table_constraints tc
                           JOIN information_schema.key_column_usage kcu
                             ON tc.constraint_name = kcu.constraint_name
                            AND tc.table_schema = kcu.table_schema
                           JOIN information_schema.constraint_column_usage ccu
                             ON ccu.constraint_name = tc.constraint_name
                            AND ccu.constraint_schema = tc.table_schema
                           WHERE tc.constraint_type = 'FOREIGN KEY';
                           """;

        var items = new List<(string Name, string SourceTable, string SourceColumn, string ReferenceTable, string ReferenceColumn)>();

        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sourceTable = $"{reader.GetString(1)}.{reader.GetString(2)}";
            var referenceTable = $"{reader.GetString(3)}.{reader.GetString(4)}";

            items.Add((reader.GetString(0), sourceTable, reader.GetString(5), referenceTable, reader.GetString(6)));
        }

        return items
            .GroupBy(static x => (x.Name, x.SourceTable, x.ReferenceTable))
            .Select(group => new ForeignKeySchema(
                group.Key.Name,
                group.Key.SourceTable,
                group.Select(static x => x.SourceColumn).ToArray(),
                group.Key.ReferenceTable,
                group.Select(static x => x.ReferenceColumn).ToArray()))
            .ToArray();
    }

    private static async Task<IReadOnlyList<(string Name, string TableName, IReadOnlyList<string> Columns)>> DiscoverUniqueConstraintsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT tc.constraint_name,
                                  tc.table_schema,
                                  tc.table_name,
                                  kcu.column_name
                           FROM information_schema.table_constraints tc
                           JOIN information_schema.key_column_usage kcu
                             ON tc.constraint_name = kcu.constraint_name
                            AND tc.table_schema = kcu.table_schema
                           WHERE tc.constraint_type = 'UNIQUE';
                           """;

        var items = new List<(string Name, string TableName, string Column)>();

        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add((reader.GetString(0), $"{reader.GetString(1)}.{reader.GetString(2)}", reader.GetString(3)));
        }

        return items
            .GroupBy(static x => (x.Name, x.TableName))
            .Select(group => (group.Key.Name, group.Key.TableName, (IReadOnlyList<string>)group.Select(static x => x.Column).ToArray()))
            .ToArray();
    }
}
