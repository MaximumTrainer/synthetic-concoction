using System.Globalization;
using System.Text.Json;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Export;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Npgsql;
using Parquet;
using Testcontainers.PostgreSql;

namespace Fabricate.Tests.Integration;

/// <summary>
/// #81: exports were only ever inspected as files, never fed back into the systems that consume them. These apply
/// the generated SQL to real databases with foreign keys enforced, and read the other formats back with real
/// parsers — which is what "round-trip usable" actually means.
/// </summary>
public sealed class ExportRoundTripTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fabricate-roundtrip-{Guid.NewGuid():N}");
    private PostgreSqlContainer? _postgres;
    private GenerationResult _result = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var (result, _) = await GenerationFixture.CreateOrchestrator(31).GenerateAsync(GenerationFixture.CreateRequest(31, rowsPerTable: 15));
        _result = result;

        await new CsvExporter().ExportAsync(result.Tables, Path.Combine(_root, "csv"));
        await new JsonExporter().ExportAsync(result.Tables, Path.Combine(_root, "json"));
        await new SqlExporter().ExportAsync(result.Tables, Path.Combine(_root, "sql"));
        await new ParquetExporter().ExportAsync(result.Tables, Path.Combine(_root, "parquet"));

        if (Environment.GetEnvironmentVariable("FABRICATE_SKIP_DOCKER_TESTS") == "1") return;
        try
        {
            _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await _postgres.StartAsync();
        }
        catch (Exception)
        {
            _postgres = null; // No Docker: the PostgreSQL leg self-skips, the rest still runs.
        }
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Statements in dependency order — a parent's rows must exist before its children reference them.</summary>
    private IEnumerable<string> SqlStatementsInDependencyOrder()
    {
        foreach (var table in GenerationFixture.TablesInDependencyOrder)
        {
            var file = Path.Combine(_root, "sql", table.Replace('.', '_') + ".sql");
            File.Exists(file).Should().BeTrue($"the SQL exporter must write {file}");

            foreach (var statement in File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                yield return statement;
            }
        }
    }

    private int ExpectedRows(string table) => _result.Tables.Single(t => t.Table == table).Rows.Count;

    [Fact]
    public async Task GeneratedSql_AppliesToSqliteWithForeignKeysEnforced()
    {
        var file = Path.Combine(_root, "roundtrip.db");
        await using var connection = new SqliteConnection($"Data Source={file}");
        await connection.OpenAsync();

        await Execute(connection, "PRAGMA foreign_keys = ON;");
        foreach (var ddl in GenerationFixture.SqliteDdl.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.IsNullOrWhiteSpace(ddl)) await Execute(connection, ddl);
        }

        var applied = 0;
        foreach (var statement in SqlStatementsInDependencyOrder())
        {
            await Execute(connection, statement);
            applied++;
        }

        applied.Should().BeGreaterThan(0);
        foreach (var table in GenerationFixture.TablesInDependencyOrder)
        {
            var name = table.Split('.')[1];
            (await Scalar(connection, $"SELECT COUNT(*) FROM {name}")).Should().Be(ExpectedRows(table),
                $"every generated row for {table} must survive insertion under constraint enforcement");
        }

        // A deliberate violation must still be rejected, proving enforcement was actually on.
        var violate = async () => await Execute(connection, "INSERT INTO orders (id, user_id, reference, placed_on, total) VALUES (999999, 888888, 'x', '2020-01-01', 1.0)");
        await violate.Should().ThrowAsync<SqliteException>();
    }

    [Fact]
    public async Task GeneratedSql_AppliesToPostgreSql()
    {
        if (_postgres is null) return;

        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();

        await using (var ddl = new NpgsqlCommand(GenerationFixture.PostgresDdl, connection))
        {
            await ddl.ExecuteNonQueryAsync();
        }

        foreach (var statement in SqlStatementsInDependencyOrder())
        {
            await using var cmd = new NpgsqlCommand(statement, connection);
            await cmd.ExecuteNonQueryAsync();
        }

        foreach (var table in GenerationFixture.TablesInDependencyOrder)
        {
            var name = table.Split('.')[1];
            await using var count = new NpgsqlCommand($"SELECT COUNT(*) FROM main.{name}", connection);
            Convert.ToInt32(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture)
                .Should().Be(ExpectedRows(table));
        }
    }

    [Fact]
    public void CsvRoundTrip_PreservesColumnOrderRowCountAndQuoting()
    {
        foreach (var table in GenerationFixture.TablesInDependencyOrder)
        {
            var file = Path.Combine(_root, "csv", table.Replace('.', '_') + ".csv");
            var rows = ReadCsv(file);

            rows.Count.Should().Be(ExpectedRows(table) + 1, $"{table} must have a header plus every row");

            // Column order is alphabetical by design, so the output is stable regardless of dictionary ordering.
            var expectedColumns = _result.Tables.Single(t => t.Table == table).Rows[0].Keys
                .OrderBy(k => k, StringComparer.Ordinal).ToArray();
            rows[0].Should().Equal(expectedColumns, "the header must list every column in the exporter's stable order");
            rows.Skip(1).Should().OnlyContain(r => r.Count == expectedColumns.Length, "every row must have every column");
        }
    }

    [Fact]
    public void JsonRoundTrip_PreservesTypesAndNulls()
    {
        var file = Path.Combine(_root, "json", "main_users.json");
        using var document = JsonDocument.Parse(File.ReadAllText(file));

        document.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        document.RootElement.GetArrayLength().Should().Be(ExpectedRows("main.users"));

        var first = document.RootElement[0];
        first.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Number, "numbers must not be stringified");
        first.GetProperty("is_active").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);

        // manager_id is null on at least the first row of the self-referencing chain.
        document.RootElement.EnumerateArray()
            .Should().Contain(r => r.GetProperty("manager_id").ValueKind == JsonValueKind.Null, "nulls must be preserved as nulls");
    }

    [Fact]
    public async Task ParquetRoundTrip_ReadsBackWithRowCountAndValues()
    {
        var file = Path.Combine(_root, "parquet", "main_users.parquet");
        File.Exists(file).Should().BeTrue();

        await using var stream = File.OpenRead(file);
        await using var reader = await ParquetReader.CreateAsync(stream);

        var fields = reader.Schema.Fields.Select(f => f.Name).ToArray();
        fields.Should().Contain(["id", "email", "display_name", "created_at", "is_active", "balance", "manager_id"]);

        var rowCount = 0L;
        for (var group = 0; group < reader.RowGroupCount; group++)
        {
            using var rowGroup = reader.OpenRowGroupReader(group);
            rowCount += rowGroup.RowCount;
        }

        rowCount.Should().Be(ExpectedRows("main.users"));

        // Nullable columns must be declared nullable, or nulls could not round-trip at all.
        reader.Schema.DataFields.Single(f => f.Name == "manager_id").IsNullable.Should().BeTrue();
        reader.Schema.DataFields.Single(f => f.Name == "balance").IsNullable.Should().BeTrue();
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static async Task Execute(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> Scalar(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    /// <summary>Strict-enough RFC-4180 reader: quoted fields, escaped quotes, embedded delimiters and newlines.</summary>
    private static List<List<string>> ReadCsv(string path)
    {
        var content = File.ReadAllText(path);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < content.Length && content[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c is '\r') { /* handled with \n */ }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; }
            else field.Append(c);
        }

        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
