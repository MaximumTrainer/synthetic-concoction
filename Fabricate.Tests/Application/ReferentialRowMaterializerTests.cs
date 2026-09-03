using Fabricate.Application.Generation;
using Fabricate.Application.Schema;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class ReferentialRowMaterializerTests
{
    private static ReferentialRowMaterializer BuildMaterializer(int seed = 42)
    {
        var random = new DeterministicRandomService(seed);
        var registry = new GeneratorRegistry();
        registry.RegisterDefaults(random);
        return new ReferentialRowMaterializer(registry, random);
    }

    [Fact]
    public async Task MaterializeAsync_ShouldSampleFromDistribution_WhenColumnRuleHasDistribution()
    {
        var table = new TableSchema("main", "orders",
        [
            new ColumnSchema("status", "text", DataKind.String, false, false, true, null, null, null, null)
        ],
        [], [], [], []);

        var rules = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule
                {
                    Table = "main.orders",
                    Columns =
                    [
                        new ColumnRule
                        {
                            Column = "status",
                            Distribution = new Dictionary<string, double>
                            {
                                ["pending"] = 0.5,
                                ["shipped"] = 0.3,
                                ["delivered"] = 0.2
                            }
                        }
                    ]
                }
            ]
        };

        var materializer = BuildMaterializer();
        var data = await materializer.MaterializeAsync(table, 100, rules,
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>());

        var statuses = data.Rows.Select(r => (string?)r["status"]).ToArray();
        statuses.Should().AllSatisfy(s => s.Should().BeOneOf("pending", "shipped", "delivered"));
        // With 100 rows and weighted distribution, all three values should appear.
        statuses.Should().Contain("pending").And.Contain("shipped").And.Contain("delivered");
    }

    [Fact]
    public async Task MaterializeAsync_ShouldUseDataKindOverride_WhenStrategySpecifiesEmail()
    {
        var table = new TableSchema("main", "contacts",
        [
            new ColumnSchema("contact_address", "text", DataKind.String, false, false, true, null, null, null, null)
        ],
        [], [], [], []);

        var rules = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule
                {
                    Table = "main.contacts",
                    Columns =
                    [
                        new ColumnRule { Column = "contact_address", Strategy = "Email" }
                    ]
                }
            ]
        };

        var materializer = BuildMaterializer();
        var data = await materializer.MaterializeAsync(table, 10, rules,
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>());

        var values = data.Rows.Select(r => (string?)r["contact_address"]).ToArray();
        values.Should().AllSatisfy(v => v.Should().Contain("@"),
            "strategy override to Email should produce email-format values");
    }

    [Fact]
    public async Task MaterializeAsync_ShouldUseFixedValue_WhenColumnRuleSpecifiesFixedValue()
    {
        var table = new TableSchema("main", "settings",
        [
            new ColumnSchema("region", "text", DataKind.String, false, false, true, null, null, null, null)
        ],
        [], [], [], []);

        var rules = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule
                {
                    Table = "main.settings",
                    Columns = [new ColumnRule { Column = "region", FixedValue = "us-east-1" }]
                }
            ]
        };

        var materializer = BuildMaterializer();
        var data = await materializer.MaterializeAsync(table, 5, rules,
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>());

        data.Rows.Should().AllSatisfy(row => row["region"].Should().Be("us-east-1"));
    }
}
