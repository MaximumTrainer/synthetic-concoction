using Fabricate.Application.Abstractions;
using Fabricate.Application.Compliance;
using Fabricate.Application.Constraints;
using Fabricate.Application.Generation;
using Fabricate.Application.Orchestration;
using Fabricate.Application.Planning;
using Fabricate.Application.Schema;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Export;
using FluentAssertions;
using Xunit;

namespace Fabricate.Tests.Application;

public sealed class StreamingExportTests
{
    private static DatabaseSchema SimpleSchema() => new("main",
    [
        new TableSchema("main", "users",
        [
            new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null),
            new ColumnSchema("name", "text", DataKind.Name, false, false, true, 100, null, null, null)
        ],
        PrimaryKey: ["id"],
        ForeignKeys: [],
        UniqueConstraints: [],
        Indexes: [])
    ]);

    private static SyntheticDataOrchestrator BuildOrchestrator(DatabaseSchema schema)
    {
        var provider = new StubSchemaProvider(schema);
        var schemaService = new SchemaDiscoveryService(provider);
        var random = new DeterministicRandomService(42);
        var registry = new GeneratorRegistry();
        registry.RegisterDefaults(random);
        return new SyntheticDataOrchestrator(
            schemaService,
            new DependencyGraphPlanner(),
            new ReferentialRowMaterializer(registry, random),
            new ConstraintEvaluator(),
            new DefaultSensitiveFieldPolicy());
    }

    [Fact]
    public async Task StreamingAsync_Csv_ProducesCorrectRowCount()
    {
        var schema = SimpleSchema();
        var orchestrator = BuildOrchestrator(schema);
        var exporter = new CsvExporter();
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        try
        {
            var request = new GenerationRequest(schema, new Dictionary<string, int>(StringComparer.Ordinal) { ["main.users"] = 20 }, 42);
            var summary = await orchestrator.GenerateStreamingAsync(request, exporter, dir);

            summary.RowCount.Should().Be(20);
            summary.TableCount.Should().Be(1);

            var csvFile = Path.Combine(dir, "main_users.csv");
            File.Exists(csvFile).Should().BeTrue();
            var lines = await File.ReadAllLinesAsync(csvFile);
            // header row + 20 data rows
            lines.Should().HaveCount(21);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task StreamingAsync_Json_WritesValidJsonArray()
    {
        var schema = SimpleSchema();
        var orchestrator = BuildOrchestrator(schema);
        var exporter = new JsonExporter();
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        try
        {
            var request = new GenerationRequest(schema, new Dictionary<string, int>(StringComparer.Ordinal) { ["main.users"] = 10 }, 42);
            var summary = await orchestrator.GenerateStreamingAsync(request, exporter, dir);

            summary.RowCount.Should().Be(10);

            var jsonFile = Path.Combine(dir, "main_users.json");
            File.Exists(jsonFile).Should().BeTrue();
            var content = await File.ReadAllTextAsync(jsonFile);
            content.TrimStart().Should().StartWith("[");
            content.TrimEnd().Should().EndWith("]");
            content.Should().Contain("\"id\"");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task StreamingAsync_ProducesDeterministicOutput()
    {
        var schema = SimpleSchema();
        var dir1 = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var dir2 = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

        try
        {
            var request = new GenerationRequest(schema, new Dictionary<string, int>(StringComparer.Ordinal) { ["main.users"] = 5 }, 7);

            var summary1 = await orchestrator().GenerateStreamingAsync(request, new CsvExporter(), dir1);
            var summary2 = await orchestrator().GenerateStreamingAsync(request, new CsvExporter(), dir2);

            summary1.RowCount.Should().Be(summary2.RowCount);

            var csv1 = await File.ReadAllTextAsync(Path.Combine(dir1, "main_users.csv"));
            var csv2 = await File.ReadAllTextAsync(Path.Combine(dir2, "main_users.csv"));
            csv1.Should().Be(csv2);
        }
        finally
        {
            if (Directory.Exists(dir1)) Directory.Delete(dir1, recursive: true);
            if (Directory.Exists(dir2)) Directory.Delete(dir2, recursive: true);
        }

        SyntheticDataOrchestrator orchestrator() => BuildOrchestrator(schema);
    }

    private sealed class StubSchemaProvider(DatabaseSchema schema) : ISchemaProvider
    {
        public string ProviderName => "stub";
        public Task<DatabaseSchema> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult(schema);
    }
}
