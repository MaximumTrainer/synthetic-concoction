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

/// <summary>
/// Integration tests for FK referential integrity, including self-referencing FK backfill.
/// </summary>
public sealed class ReferentialIntegrityTests
{
    private static SyntheticDataOrchestrator BuildOrchestrator(long seed = 42)
    {
        var random = new DeterministicRandomService(seed);
        var registry = new GeneratorRegistry();
        registry.RegisterDefaults(random);

        return new SyntheticDataOrchestrator(
            new SchemaDiscoveryService(new StubSchemaProvider(new DatabaseSchema("_stub", []))),
            new DependencyGraphPlanner(),
            new ReferentialRowMaterializer(registry, random),
            new ConstraintEvaluator(),
            new DefaultSensitiveFieldPolicy());
    }

    [Fact]
    public async Task GenerateAsync_ChildFkValues_AreSubsetOfParentPkValues()
    {
        // Arrange — parent (users) with 5 rows; child (orders) with 10 rows pointing to user.id
        var schema = new DatabaseSchema("test",
        [
            new TableSchema("main", "users",
            [
                new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null),
            ],
            ["id"], [], [], []),
            new TableSchema("main", "orders",
            [
                new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null),
                new ColumnSchema("user_id", "int", DataKind.Integer, false, false, false, null, null, null, null),
            ],
            ["id"],
            [new ForeignKeySchema("fk_orders_users", "main.orders", ["user_id"], "main.users", ["id"])],
            [], [])
        ]);

        var orchestrator = BuildOrchestrator(42);
        var request = new GenerationRequest(schema,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["main.users"] = 5, ["main.orders"] = 10 },
            42);

        // Act
        var (result, _) = await orchestrator.GenerateAsync(request);

        // Assert — every order.user_id must appear in users.id
        var userIds = result.Tables.Single(t => t.Table == "main.users").Rows
            .Select(r => r["id"])
            .ToHashSet();
        var orderUserIds = result.Tables.Single(t => t.Table == "main.orders").Rows
            .Select(r => r["user_id"]);

        orderUserIds.Should().OnlyContain(uid => userIds.Contains(uid),
            "every order.user_id must reference a valid users.id");
    }

    [Fact]
    public async Task GenerateAsync_SelfRefFkWithNullableColumn_BackfillsChainAndRootIsNull()
    {
        // Arrange — employees with nullable manager_id self-ref (nullable → root can be null)
        var schema = new DatabaseSchema("test",
        [
            new TableSchema("main", "employees",
            [
                new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null),
                new ColumnSchema("manager_id", "int", DataKind.Integer, true, false, false, null, null, null, null), // nullable
            ],
            ["id"],
            [new ForeignKeySchema("fk_emp_manager", "main.employees", ["manager_id"], "main.employees", ["id"])],
            [], [])
        ]);

        var orchestrator = BuildOrchestrator(42);
        var request = new GenerationRequest(schema,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["main.employees"] = 5 },
            42);

        // Act
        var (result, _) = await orchestrator.GenerateAsync(request);

        var rows = result.Tables.Single(t => t.Table == "main.employees").Rows;
        rows.Should().HaveCount(5);

        // Root row (index 0) should have null manager_id
        rows[0]["manager_id"].Should().BeNull("root employee has no manager");

        // All non-root rows must reference an earlier row's id (valid FK)
        var employeeIds = rows.Select(r => r["id"]).ToList();
        for (var i = 1; i < rows.Count; i++)
        {
            var managerId = rows[i]["manager_id"];
            managerId.Should().NotBeNull($"employee at row {i} should have a manager");
            employeeIds.Take(i).Should().Contain(managerId,
                $"employee at row {i} should reference an earlier employee as manager");
        }
    }

    [Fact]
    public async Task GenerateAsync_SelfRefFkNonNullable_EmitsValidationIssue()
    {
        // Arrange — non-nullable self-ref FK → root row cannot be null → validation issue expected
        var schema = new DatabaseSchema("test",
        [
            new TableSchema("main", "nodes",
            [
                new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null),
                new ColumnSchema("parent_id", "int", DataKind.Integer, false, false, false, null, null, null, null), // NOT nullable
            ],
            ["id"],
            [new ForeignKeySchema("fk_nodes_parent", "main.nodes", ["parent_id"], "main.nodes", ["id"])],
            [], [])
        ]);

        var orchestrator = BuildOrchestrator(42);
        var request = new GenerationRequest(schema,
            new Dictionary<string, int>(StringComparer.Ordinal) { ["main.nodes"] = 3 },
            42);

        // Act
        var (result, _) = await orchestrator.GenerateAsync(request);

        // The root row on non-nullable self-ref should produce a validation issue
        result.ValidationIssues.Should().Contain(i => i.Table == "main.nodes" && i.Column == "parent_id",
            "non-nullable self-ref root row should emit a validation issue");
    }

    private sealed class StubSchemaProvider(DatabaseSchema schema) : ISchemaProvider
    {
        public string ProviderName => "stub";
        public Task<DatabaseSchema> DiscoverAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(schema);
    }
}
