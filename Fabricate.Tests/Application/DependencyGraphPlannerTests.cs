using Fabricate.Application.Planning;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class DependencyGraphPlannerTests
{
    [Fact]
    public void BuildPlan_AcyclicSchema_ShouldTopologicallySort()
    {
        var planner = new DependencyGraphPlanner();
        var schema = new DatabaseSchema("demo", [
            Table("main", "users"),
            Table("main", "orders", [Fk("fk_orders_users", "main.orders", "main.users", "user_id", "id")])
        ]);

        var plan = planner.BuildPlan(schema);
        var ordered = plan.OrderedTables.ToArray();

        plan.Cycles.Should().BeEmpty();
        Array.IndexOf(ordered, "main.users").Should().BeLessThan(Array.IndexOf(ordered, "main.orders"));
    }

    [Fact]
    public void BuildPlan_CyclicSchema_ShouldReportCycles()
    {
        var planner = new DependencyGraphPlanner();
        var schema = new DatabaseSchema("demo", [
            Table("main", "a", [Fk("fk_a_b", "main.a", "main.b", "b_id", "id")]),
            Table("main", "b", [Fk("fk_b_a", "main.b", "main.a", "a_id", "id")])
        ]);

        var plan = planner.BuildPlan(schema);

        plan.Cycles.Should().HaveCount(1);
        plan.Diagnostics.Should().ContainSingle();
    }

    private static TableSchema Table(string schema, string name, IReadOnlyList<ForeignKeySchema>? fks = null)
        => new(
            schema,
            name,
            [new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null)],
            ["id"],
            fks ?? [],
            [new UniqueConstraintSchema($"uq_{name}_id", ["id"])],
            []);

    private static ForeignKeySchema Fk(string name, string sourceTable, string referenceTable, string sourceColumn, string referenceColumn)
        => new(name, sourceTable, [sourceColumn], referenceTable, [referenceColumn]);
}
