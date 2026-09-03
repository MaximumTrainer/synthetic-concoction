using Fabricate.Application.Configuration;
using Fabricate.Domain.Models;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class RuleConfigurationServiceTests
{
    [Fact]
    public void Merge_ShouldApplyUserPrecedence()
    {
        var service = new RuleConfigurationService();
        var defaults = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule
                {
                    Table = "main.users",
                    Columns = [new ColumnRule { Column = "email", Strategy = "pseudonymize" }]
                }
            ]
        };

        var schema = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule
                {
                    Table = "main.users",
                    Columns = [new ColumnRule { Column = "email", Strategy = "synthesize" }]
                }
            ]
        };

        var user = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule
                {
                    Table = "main.users",
                    Columns = [new ColumnRule { Column = "email", Strategy = "fixed", FixedValue = "x@x.com" }]
                }
            ]
        };

        var merged = service.Merge(defaults, schema, user);

        merged.Tables.Should().HaveCount(1);
        merged.Tables[0].Columns.Should().ContainSingle();
        merged.Tables[0].Columns[0].FixedValue.Should().Be("x@x.com");
    }

    [Fact]
    public void Validate_ShouldReturnError_WhenStrategyIsUnknownDataKind()
    {
        var service = new RuleConfigurationService();
        var config = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule
                {
                    Table = "main.users",
                    Columns = [new ColumnRule { Column = "name", Strategy = "NotARealDataKind" }]
                }
            ]
        };

        var errors = service.Validate(config);

        errors.Should().ContainSingle(e => e.Contains("NotARealDataKind"));
    }

    [Fact]
    public void Validate_ShouldReturnNoErrors_WhenStrategyIsValidDataKind()
    {
        var service = new RuleConfigurationService();
        var config = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule
                {
                    Table = "main.users",
                    Columns = [new ColumnRule { Column = "contact", Strategy = "Email" }]
                }
            ]
        };

        var errors = service.Validate(config);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_ShouldReturnNoErrors_WhenStrategyIsNull()
    {
        var service = new RuleConfigurationService();
        var config = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule
                {
                    Table = "main.users",
                    Columns = [new ColumnRule { Column = "status" }]
                }
            ]
        };

        var errors = service.Validate(config);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_ShouldReturnError_WhenRowCountIsZero()
    {
        var service = new RuleConfigurationService();
        var config = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule { Table = "main.orders", RowCount = 0, Columns = [] }
            ]
        };

        var errors = service.Validate(config);

        errors.Should().Contain(e => e.Contains("RowCount"));
    }

    [Fact]
    public void Validate_ShouldReturnError_WhenRowCountIsNegative()
    {
        var service = new RuleConfigurationService();
        var config = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule { Table = "main.orders", RowCount = -5, Columns = [] }
            ]
        };

        var errors = service.Validate(config);

        errors.Should().Contain(e => e.Contains("RowCount"));
    }

    [Fact]
    public void Validate_ShouldReturnNoErrors_WhenRowCountIsPositive()
    {
        var service = new RuleConfigurationService();
        var config = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule { Table = "main.orders", RowCount = 50, Columns = [] }
            ]
        };

        var errors = service.Validate(config);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_ShouldReturnError_WhenMinValueExceedsMaxValueNumeric()
    {
        var service = new RuleConfigurationService();
        var config = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule
                {
                    Table = "main.orders",
                    Columns = [new ColumnRule { Column = "amount", MinValue = "100", MaxValue = "50" }]
                }
            ]
        };

        var errors = service.Validate(config);

        errors.Should().Contain(e => e.Contains("MinValue") && e.Contains("MaxValue"));
    }

    [Fact]
    public void Validate_ShouldReturnNoErrors_WhenMinMaxValid()
    {
        var service = new RuleConfigurationService();
        var config = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule
                {
                    Table = "main.orders",
                    Columns = [new ColumnRule { Column = "amount", MinValue = "10", MaxValue = "1000" }]
                }
            ]
        };

        var errors = service.Validate(config);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_ShouldReturnError_WhenPatternInvalid()
    {
        var service = new RuleConfigurationService();
        var config = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule
                {
                    Table = "main.users",
                    Columns = [new ColumnRule { Column = "code", Pattern = "[invalid(" }]
                }
            ]
        };

        var errors = service.Validate(config);

        errors.Should().Contain(e => e.Contains("Pattern") || e.Contains("regular expression"));
    }

    [Fact]
    public void Validate_ShouldReturnNoErrors_WhenPatternValid()
    {
        var service = new RuleConfigurationService();
        var config = new RuleConfiguration
        {
            Version = "1",
            Tables =
            [
                new TableRule
                {
                    Table = "main.users",
                    Columns = [new ColumnRule { Column = "code", Pattern = "[A-Z]{3}[0-9]{4}" }]
                }
            ]
        };

        var errors = service.Validate(config);

        errors.Should().BeEmpty();
    }
}
