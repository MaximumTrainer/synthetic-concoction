using Fabricate.Application.Abstractions;
using Fabricate.Application.Generation;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class GenerationPlanServiceTests
{
    private static DatabaseSchema BuildSchema() => new("testdb",
    [
        new TableSchema("public", "customers",
        [
            new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null),
            new ColumnSchema("email", "varchar", DataKind.Email, false, false, false, 255, null, null, null),
            new ColumnSchema("name", "varchar", DataKind.Name, false, false, false, 100, null, null, null),
        ],
        ["id"], [], [], [])
    ]);

    [Fact]
    public void BuildDiagnosticsReport_ShouldReturnEntryForEachColumn()
    {
        var registry = new GeneratorRegistry();
        var random = new DeterministicRandomService(1);
        registry.RegisterDefaults(random);

        var service = new GenerationPlanService(registry);
        var schema = BuildSchema();

        var report = service.BuildDiagnosticsReport(schema);

        report.Columns.Should().HaveCount(3);
        report.Columns.Should().AllSatisfy(c =>
        {
            c.Table.Should().Be("public.customers");
            c.Column.Should().NotBeNullOrEmpty();
            c.ResolvedStrategy.Should().NotBeNullOrEmpty();
        });
    }

    [Fact]
    public void BuildDiagnosticsReport_ShouldResolveEmailKind()
    {
        var registry = new GeneratorRegistry();
        var random = new DeterministicRandomService(1);
        registry.RegisterDefaults(random);

        var service = new GenerationPlanService(registry);
        var schema = BuildSchema();

        var report = service.BuildDiagnosticsReport(schema);

        var emailEntry = report.Columns.First(c => c.Column == "email");
        emailEntry.DataKind.Should().Be(DataKind.Email);
    }

    [Fact]
    public void BuildDiagnosticsReport_ShouldApplyRuleOverride()
    {
        var registry = new GeneratorRegistry();
        var random = new DeterministicRandomService(1);
        registry.RegisterDefaults(random);

        var service = new GenerationPlanService(registry);
        var schema = BuildSchema();

        var rules = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule
                {
                    Table = "public.customers",
                    Columns = [new ColumnRule { Column = "email", Strategy = "uuid" }]
                }
            ]
        };

        var report = service.BuildDiagnosticsReport(schema, rules);

        var emailEntry = report.Columns.First(c => c.Column == "email");
        emailEntry.ResolvedStrategy.Should().Be("uuid");
        emailEntry.StrategyProvenance.Should().Contain("column-rule");
    }
}
