using Fabricate.Infrastructure.Configuration;
using Fabricate.Infrastructure.Schema;
using FluentAssertions;
using Npgsql;
using Microsoft.Extensions.Options;

namespace Fabricate.Tests.Integration;

public sealed class PostgreSqlSchemaProviderTests
{
    [Fact]
    public async Task DiscoverAsync_ShouldReturnCanonicalSchema_WhenConnectionProvided()
    {
        var connectionString = Environment.GetEnvironmentVariable("FABRICATE_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        var schemaName = $"test_{Guid.NewGuid():N}";

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            var sql = $"""
                       CREATE SCHEMA "{schemaName}";
                       CREATE TABLE "{schemaName}".users (id SERIAL PRIMARY KEY, email TEXT UNIQUE NOT NULL);
                       CREATE TABLE "{schemaName}".orders (id SERIAL PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES "{schemaName}".users(id));
                       """;
            await using var cmd = new NpgsqlCommand(sql, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        try
        {
            var provider = new PostgreSqlSchemaProvider(Options.Create(new SchemaProviderOptions
            {
                Provider = "postgres",
                ConnectionString = connectionString,
                DatabaseName = "fixture"
            }));

            var schema = await provider.DiscoverAsync();
            schema.Tables.Should().Contain(t => t.QualifiedName == $"{schemaName}.users");
            schema.Tables.Should().Contain(t => t.QualifiedName == $"{schemaName}.orders");
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(connectionString);
            await cleanup.OpenAsync();
            await using var dropCmd = new NpgsqlCommand($"DROP SCHEMA \"{schemaName}\" CASCADE;", cleanup);
            await dropCmd.ExecuteNonQueryAsync();
        }
    }
}
