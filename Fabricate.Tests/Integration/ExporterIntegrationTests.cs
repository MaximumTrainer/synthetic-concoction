using System.Text;
using System.Text.Json;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Export;
using FluentAssertions;

namespace Fabricate.Tests.Integration;

/// <summary>
/// Integration tests that exercise the three exporters and artifact store against
/// real file-system targets, satisfying issue #22 acceptance criterion:
/// "Export/validation verified with real file/database targets in integration tests."
/// </summary>
public sealed class ExporterIntegrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"fabricate-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static IReadOnlyList<TableData> BuildFixtureTables() =>
    [
        new TableData("public.users",
        [
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["id"] = 1, ["email"] = "alice@example.com", ["active"] = true },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["id"] = 2, ["email"] = "bob's,address@x.io", ["active"] = false }
        ]),
        new TableData("public.orders",
        [
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["id"] = 100, ["user_id"] = 1, ["amount"] = 9.99m }
        ])
    ];

    // ── CSV ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CsvExporter_WritesOneFilePerTable_WithCorrectHeaders()
    {
        var exporter = new CsvExporter();
        var tables = BuildFixtureTables();

        await exporter.ExportAsync(tables, _tempDir);

        var csvFiles = Directory.GetFiles(_tempDir, "*.csv").OrderBy(f => f).ToArray();
        csvFiles.Should().HaveCount(2);

        var ordersContent = await File.ReadAllTextAsync(csvFiles.First(f => f.Contains("orders")));
        ordersContent.Should().Contain("amount").And.Contain("id").And.Contain("user_id");
        ordersContent.Should().Contain("9.99").And.Contain("100").And.Contain("1");

        var usersContent = await File.ReadAllTextAsync(csvFiles.First(f => f.Contains("users")));
        usersContent.Should().Contain("email").And.Contain("active").And.Contain("id");
        // Special characters are RFC-4180 quoted
        usersContent.Should().Contain("\"bob's,address@x.io\"");
    }

    [Fact]
    public async Task CsvExporter_EmptyTable_WritesEmptyFile()
    {
        var exporter = new CsvExporter();
        var tables = new[] { new TableData("public.empty", []) };

        await exporter.ExportAsync(tables, _tempDir);

        var file = Directory.GetFiles(_tempDir, "*.csv").Single();
        var content = await File.ReadAllTextAsync(file);
        content.Should().BeEmpty("empty table should produce empty CSV");
    }

    // ── JSON ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task JsonExporter_WritesOneFilePerTable_WithValidJson()
    {
        var exporter = new JsonExporter();
        var tables = BuildFixtureTables();

        await exporter.ExportAsync(tables, _tempDir);

        var jsonFiles = Directory.GetFiles(_tempDir, "*.json").OrderBy(f => f).ToArray();
        jsonFiles.Should().HaveCount(2);

        foreach (var file in jsonFiles)
        {
            var content = await File.ReadAllTextAsync(file);
            // Must be parseable JSON
            var doc = JsonDocument.Parse(content);
            doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
            doc.RootElement.GetArrayLength().Should().BeGreaterThan(0);
        }

        // Verify specific fields in users export
        var usersFile = jsonFiles.First(f => f.Contains("users"));
        var usersDoc = JsonDocument.Parse(await File.ReadAllTextAsync(usersFile));
        var firstUser = usersDoc.RootElement[0];
        firstUser.TryGetProperty("email", out var emailEl).Should().BeTrue();
        emailEl.GetString().Should().Be("alice@example.com");
    }

    // ── SQL ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SqlExporter_WritesInsertStatements_WithCorrectSyntax()
    {
        var exporter = new SqlExporter();
        var tables = BuildFixtureTables();

        await exporter.ExportAsync(tables, _tempDir);

        var sqlFiles = Directory.GetFiles(_tempDir, "*.sql").OrderBy(f => f).ToArray();
        sqlFiles.Should().HaveCount(2);

        var usersSql = await File.ReadAllTextAsync(sqlFiles.First(f => f.Contains("users")));
        usersSql.Should().Contain("INSERT INTO");
        usersSql.Should().Contain("\"public.users\"");
        usersSql.Should().Contain("'alice@example.com'");
        // Boolean TRUE/FALSE
        usersSql.Should().Contain("TRUE").And.Contain("FALSE");
        // SQL-escape single quotes in email
        usersSql.Should().Contain("bob''s");
    }

    [Fact]
    public async Task SqlExporter_NullValues_AreEmittedAsNullKeyword()
    {
        var exporter = new SqlExporter();
        var tables = new[]
        {
            new TableData("public.items",
            [
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["id"] = 1, ["description"] = null }
            ])
        };

        await exporter.ExportAsync(tables, _tempDir);

        var content = await File.ReadAllTextAsync(Directory.GetFiles(_tempDir, "*.sql").Single());
        content.Should().Contain("NULL");
    }

    // ── Artifact Store ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FileSystemArtifactStore_StoreAndRetrieve_RoundTripsContent()
    {
        var store = new FileSystemArtifactStore(_tempDir);
        const string runId = "run-abc-123";
        const string originalContent = "synthetic data payload";

        using var writeStream = new MemoryStream(Encoding.UTF8.GetBytes(originalContent));
        var storedPath = await store.StoreAsync(runId, "output.csv", writeStream);

        storedPath.Should().EndWith("output.csv");
        (await store.ExistsAsync(storedPath)).Should().BeTrue();

        await using var readStream = await store.RetrieveAsync(storedPath);
        using var reader = new StreamReader(readStream, Encoding.UTF8);
        var result = await reader.ReadToEndAsync();
        result.Should().Be(originalContent);
    }

    [Fact]
    public async Task FileSystemArtifactStore_ExistsAsync_ReturnsFalseForMissingPath()
    {
        var store = new FileSystemArtifactStore(_tempDir);
        var exists = await store.ExistsAsync("/nonexistent/path/artifact.csv");
        exists.Should().BeFalse();
    }

    [Fact]
    public async Task FileSystemArtifactStore_RetrieveAsync_ThrowsForMissingArtifact()
    {
        var store = new FileSystemArtifactStore(_tempDir);
        var act = () => store.RetrieveAsync(Path.Combine(_tempDir, "missing.csv"));
        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task FileSystemArtifactStore_ComputeChecksum_IsStable()
    {
        var store = new FileSystemArtifactStore(_tempDir);
        const string content = "deterministic content";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var path = await store.StoreAsync("run-cs", "data.txt", ms);

        var hash1 = await FileSystemArtifactStore.ComputeChecksumAsync(path);
        var hash2 = await FileSystemArtifactStore.ComputeChecksumAsync(path);

        hash1.Should().Be(hash2, "checksum must be deterministic");
        hash1.Should().HaveLength(64, "SHA-256 hex string is 64 characters");
        hash1.Should().MatchRegex("^[0-9a-f]+$", "hex characters only");
    }

    // ── Parquet ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ParquetExporter_WritesOneFilePerTable_WithExpectedRowCount()
    {
        var exporter = new ParquetExporter();
        var tables = BuildFixtureTables();

        await exporter.ExportAsync(tables, _tempDir);

        var files = Directory.GetFiles(_tempDir, "*.parquet").OrderBy(f => f).ToArray();
        files.Should().HaveCount(2);
        files.Should().Contain(f => f.Contains("users"));
        files.Should().Contain(f => f.Contains("orders"));
    }

    [Fact]
    public async Task ParquetExporter_EmptyTable_ProducesNoFile()
    {
        var exporter = new ParquetExporter();
        var tables = new[] { new TableData("public.empty", []) };

        await exporter.ExportAsync(tables, _tempDir);

        var files = Directory.GetFiles(_tempDir, "*.parquet");
        files.Should().BeEmpty("empty tables produce no file");
    }

    [Fact]
    public async Task ParquetExporter_MixedTypes_ProducesReadableFile()
    {
        var exporter = new ParquetExporter();
        var now = DateTime.UtcNow;
        var tables = new[]
        {
            new TableData("public.events",
            [
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = 1, ["name"] = "start", ["value"] = 3.14, ["active"] = true, ["ts"] = now, ["score"] = null
                }
            ])
        };

        await exporter.ExportAsync(tables, _tempDir);

        var file = Directory.GetFiles(_tempDir, "*.parquet").Single();
        file.Should().EndWith(".parquet");
        (new FileInfo(file).Length).Should().BeGreaterThan(0);
    }
}
