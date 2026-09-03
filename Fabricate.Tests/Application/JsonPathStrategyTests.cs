using System.Text.Json;
using Fabricate.Application.Abstractions;
using Fabricate.Application.Compliance;
using Fabricate.Application.Constraints;
using Fabricate.Application.Generation;
using Fabricate.Application.Orchestration;
using Fabricate.Application.Planning;
using Fabricate.Application.Schema;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class JsonPathStrategyTests
{
    [Theory]
    [InlineData("$.email", new[] { "email" })]
    [InlineData("$.address.city", new[] { "address", "city" })]
    [InlineData("$.a.b.c", new[] { "a", "b", "c" })]
    [InlineData("$", new string[0])]
    [InlineData("", new string[0])]
    [InlineData("  ", new string[0])]
    [InlineData("$.single", new[] { "single" })]
    public void ParsePath_ReturnsCorrectSegments(string path, string[] expectedSegments)
    {
        var segments = JsonObjectGenerator.ParsePath(path);
        segments.Should().BeEquivalentTo(expectedSegments, opts => opts.WithStrictOrdering());
    }

    [Fact]
    public async Task GenerateAsync_WithJsonPaths_ProducesValidJsonWithExpectedFields()
    {
        // Arrange — a table with one JSON column whose rule has path-level strategies
        var schema = new DatabaseSchema("test",
        [
            new TableSchema("main", "profiles",
            [
                new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null),
                new ColumnSchema("metadata", "json", DataKind.Json, true, false, false, null, null, null, null),
            ],
            ["id"], [], [], [])
        ]);

        var rules = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule
                {
                    Table = "main.profiles",
                    Columns =
                    [
                        new ColumnRule
                        {
                            Column = "metadata",
                            JsonPaths =
                            [
                                new JsonPathRule { Path = "$.email", Strategy = "Email" },
                                new JsonPathRule { Path = "$.address.city", Strategy = "String" },
                                new JsonPathRule { Path = "$.score", Strategy = "Integer" },
                                new JsonPathRule { Path = "$.tag", FixedValue = "synthetic" },
                            ]
                        }
                    ]
                }
            ]
        };

        var random = new DeterministicRandomService(1);
        var registry = new GeneratorRegistry();
        registry.RegisterDefaults(random);

        var orchestrator = new SyntheticDataOrchestrator(
            new SchemaDiscoveryService(new StubSchemaProvider(schema)),
            new DependencyGraphPlanner(),
            new ReferentialRowMaterializer(registry, random),
            new ConstraintEvaluator(),
            new DefaultSensitiveFieldPolicy());

        var request = new GenerationRequest(schema,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["main.profiles"] = 3 },
            Seed: 1,
            Rules: rules);

        // Act
        var (result, _) = await orchestrator.GenerateAsync(request);

        var rows = result.Tables.Single(t => t.Table == "main.profiles").Rows;
        rows.Should().HaveCount(3);

        foreach (var row in rows)
        {
            var metadataRaw = row["metadata"];
            metadataRaw.Should().NotBeNull("metadata column should have a JSON value");
            var json = metadataRaw!.ToString()!;

            // Must be valid JSON
            var doc = JsonDocument.Parse(json);

            // Top-level "email" field should be present and look like an email
            doc.RootElement.TryGetProperty("email", out var emailEl).Should().BeTrue("$.email path should generate an email field");
            emailEl.GetString().Should().Contain("@");

            // Nested "address.city" should be present
            doc.RootElement.TryGetProperty("address", out var addrEl).Should().BeTrue("$.address path should create nested object");
            addrEl.TryGetProperty("city", out _).Should().BeTrue("$.address.city should exist inside address object");

            // "score" should be a number
            doc.RootElement.TryGetProperty("score", out var scoreEl).Should().BeTrue("$.score should be present");
            scoreEl.ValueKind.Should().Be(JsonValueKind.Number);

            // "tag" should be the fixed value
            doc.RootElement.TryGetProperty("tag", out var tagEl).Should().BeTrue("$.tag should be present");
            tagEl.GetString().Should().Be("synthetic");
        }
    }

    [Fact]
    public async Task GenerateAsync_WithJsonPaths_NullRateOmitsField()
    {
        // Arrange — a JSON column with a path that always produces null (nullRate = 1.0)
        var schema = new DatabaseSchema("test",
        [
            new TableSchema("main", "events",
            [
                new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null),
                new ColumnSchema("payload", "json", DataKind.Json, true, false, false, null, null, null, null),
            ],
            ["id"], [], [], [])
        ]);

        var rules = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule
                {
                    Table = "main.events",
                    Columns =
                    [
                        new ColumnRule
                        {
                            Column = "payload",
                            JsonPaths =
                            [
                                new JsonPathRule { Path = "$.required", Strategy = "String" },
                                new JsonPathRule { Path = "$.optional", Strategy = "String", NullRate = 1.0 },
                            ]
                        }
                    ]
                }
            ]
        };

        var random = new DeterministicRandomService(7);
        var registry = new GeneratorRegistry();
        registry.RegisterDefaults(random);

        var orchestrator = new SyntheticDataOrchestrator(
            new SchemaDiscoveryService(new StubSchemaProvider(schema)),
            new DependencyGraphPlanner(),
            new ReferentialRowMaterializer(registry, random),
            new ConstraintEvaluator(),
            new DefaultSensitiveFieldPolicy());

        var request = new GenerationRequest(schema,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["main.events"] = 5 },
            Seed: 7,
            Rules: rules);

        // Act
        var (result, _) = await orchestrator.GenerateAsync(request);

        var rows = result.Tables.Single(t => t.Table == "main.events").Rows;

        foreach (var row in rows)
        {
            var doc = JsonDocument.Parse(row["payload"]!.ToString()!);

            // "required" should always be present with a non-null value
            doc.RootElement.TryGetProperty("required", out var reqEl).Should().BeTrue();
            reqEl.GetString().Should().NotBeNullOrEmpty();

            // "optional" has nullRate=1.0 → its value should be null in the document
            doc.RootElement.TryGetProperty("optional", out var optEl).Should().BeTrue();
            optEl.ValueKind.Should().Be(JsonValueKind.Null,
                "path with nullRate=1.0 should always emit null");
        }
    }

    private sealed class StubSchemaProvider(DatabaseSchema schema) : ISchemaProvider
    {
        public string ProviderName => "stub";
        public Task<DatabaseSchema> DiscoverAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(schema);
    }
}
