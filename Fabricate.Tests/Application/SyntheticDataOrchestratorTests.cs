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

public sealed class SyntheticDataOrchestratorTests
{
    [Fact]
    public async Task GenerateAsync_ShouldProduceReferentiallyConnectedRows()
    {
        var schema = new DatabaseSchema("fixture",
        [
            new TableSchema("main", "users",
            [
                new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null),
                new ColumnSchema("email", "text", DataKind.String, false, false, true, 50, null, null, null)
            ],
            ["id"], [], [new UniqueConstraintSchema("uq_users_email", ["email"])], []),
            new TableSchema("main", "orders",
            [
                new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null),
                new ColumnSchema("user_id", "int", DataKind.Integer, false, false, false, null, null, null, null)
            ],
            ["id"],
            [new ForeignKeySchema("fk_orders_users", "main.orders", ["user_id"], "main.users", ["id"])],
            [], [])
        ]);

        var provider = new StubSchemaProvider(schema);
        var schemaService = new SchemaDiscoveryService(provider);
        var random = new DeterministicRandomService(99);
        var registry = new GeneratorRegistry();
        registry.RegisterDefaults(random);

        var orchestrator = new SyntheticDataOrchestrator(
            schemaService,
            new DependencyGraphPlanner(),
            new ReferentialRowMaterializer(registry, random),
            new ConstraintEvaluator(),
            new DefaultSensitiveFieldPolicy());

        var request = new GenerationRequest(schema,
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["main.users"] = 5,
                ["main.orders"] = 10
            },
            99);

        var (result, summary) = await orchestrator.GenerateAsync(request);

        summary.TableCount.Should().Be(2);
        result.Tables.Should().HaveCount(2);
        result.Tables.Single(t => t.Table == "main.orders").Rows.Should().HaveCount(10);
    }

    [Fact]
    public async Task GenerateAsync_ShouldApplyRedactMasking_WhenColumnMatchesSsnPattern()
    {
        var schema = new DatabaseSchema("fixture",
        [
            new TableSchema("main", "patients",
            [
                new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null),
                new ColumnSchema("ssn", "text", DataKind.String, false, false, true, null, null, null, null)
            ],
            ["id"], [], [], [])
        ]);

        var orchestrator = BuildOrchestrator(schema);
        var request = new GenerationRequest(schema,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["main.patients"] = 3 }, 42);

        var (result, _) = await orchestrator.GenerateAsync(request);

        var rows = result.Tables.Single(t => t.Table == "main.patients").Rows;
        rows.Should().AllSatisfy(row => row["ssn"].Should().Be("REDACTED"));
    }

    [Fact]
    public async Task GenerateAsync_ShouldApplyPseudonymizeMasking_WhenColumnMatchesEmailPattern()
    {
        var schema = new DatabaseSchema("fixture",
        [
            new TableSchema("main", "users",
            [
                new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null),
                new ColumnSchema("email", "text", DataKind.String, false, false, true, null, null, null, null)
            ],
            ["id"], [], [new UniqueConstraintSchema("uq_users_email", ["email"])], [])
        ]);

        var orchestrator = BuildOrchestrator(schema);
        var request = new GenerationRequest(schema,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["main.users"] = 5 }, 7);

        var (result, _) = await orchestrator.GenerateAsync(request);

        var rows = result.Tables.Single(t => t.Table == "main.users").Rows;
        rows.Should().AllSatisfy(row =>
            ((string?)row["email"]).Should().StartWith("usr_"));
    }

    [Fact]
    public async Task GenerateAsync_ShouldApplyTokenizeMasking_WhenColumnMatchesPhonePattern()
    {
        var schema = new DatabaseSchema("fixture",
        [
            new TableSchema("main", "contacts",
            [
                new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null),
                new ColumnSchema("phone_number", "text", DataKind.String, false, false, true, null, null, null, null)
            ],
            ["id"], [], [], [])
        ]);

        var orchestrator = BuildOrchestrator(schema);
        var request = new GenerationRequest(schema,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["main.contacts"] = 3 }, 5);

        var (result, _) = await orchestrator.GenerateAsync(request);

        var rows = result.Tables.Single(t => t.Table == "main.contacts").Rows;
        rows.Should().AllSatisfy(row =>
            ((string?)row["phone_number"]).Should().StartWith("TKN-"));
    }

    [Fact]
    public async Task GenerateAsync_ShouldUsePerTableRowCount_WhenSpecifiedInRules()
    {
        var schema = new DatabaseSchema("fixture",
        [
            new TableSchema("main", "customers",
            [
                new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null)
            ],
            ["id"], [], [], []),
            new TableSchema("main", "orders",
            [
                new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null),
                new ColumnSchema("customer_id", "int", DataKind.Integer, false, false, false, null, null, null, null)
            ],
            ["id"],
            [new ForeignKeySchema("fk_orders_cust", "main.orders", ["customer_id"], "main.customers", ["id"])],
            [], [])
        ]);

        var orchestrator = BuildOrchestrator(schema);
        var rules = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule { Table = "main.customers", RowCount = 5, Columns = [] },
                new TableRule { Table = "main.orders", RowCount = 25, Columns = [] }
            ]
        };

        // RequestedRowCounts has lower priority than TableRule.RowCount
        var request = new GenerationRequest(schema,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["main.customers"] = 100 },
            42,
            rules);

        var (result, _) = await orchestrator.GenerateAsync(request);

        result.Tables.Single(t => t.Table == "main.customers").Rows.Should().HaveCount(5);
        result.Tables.Single(t => t.Table == "main.orders").Rows.Should().HaveCount(25);
    }

    [Fact]
    public async Task GenerateAsync_ShouldFallbackToRequestedRowCounts_WhenNoTableRuleRowCount()
    {
        var schema = new DatabaseSchema("fixture",
        [
            new TableSchema("main", "items",
            [
                new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null)
            ],
            ["id"], [], [], [])
        ]);

        var orchestrator = BuildOrchestrator(schema);
        var request = new GenerationRequest(schema,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["main.items"] = 7 }, 1);

        var (result, _) = await orchestrator.GenerateAsync(request);

        result.Tables.Single(t => t.Table == "main.items").Rows.Should().HaveCount(7);
    }

    private static SyntheticDataOrchestrator BuildOrchestrator(DatabaseSchema schema)
    {
        var provider = new StubSchemaProvider(schema);
        var schemaService = new SchemaDiscoveryService(provider);
        var random = new DeterministicRandomService(99);
        var registry = new GeneratorRegistry();
        registry.RegisterDefaults(random);
        return new SyntheticDataOrchestrator(
            schemaService,
            new DependencyGraphPlanner(),
            new ReferentialRowMaterializer(registry, random),
            new ConstraintEvaluator(),
            new DefaultSensitiveFieldPolicy());
    }

    private sealed class StubSchemaProvider(DatabaseSchema schema) : ISchemaProvider
    {
        public string ProviderName => "stub";
        public Task<DatabaseSchema> DiscoverAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(schema);
    }
}
