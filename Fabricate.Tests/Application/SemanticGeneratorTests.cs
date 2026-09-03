using Fabricate.Application.Abstractions;
using Fabricate.Application.Generation;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class SemanticGeneratorTests
{
    private static GeneratorContext MakeContext(DataKind kind, int row = 0) =>
        new("table", "col", kind, row, null,
            new Dictionary<string, object?>(),
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>());

    [Theory]
    [InlineData(DataKind.Email)]
    [InlineData(DataKind.Phone)]
    [InlineData(DataKind.Name)]
    [InlineData(DataKind.FirstName)]
    [InlineData(DataKind.LastName)]
    [InlineData(DataKind.Address)]
    [InlineData(DataKind.PostalCode)]
    [InlineData(DataKind.CountryCode)]
    [InlineData(DataKind.Url)]
    [InlineData(DataKind.IpAddress)]
    [InlineData(DataKind.Currency)]
    [InlineData(DataKind.CompanyName)]
    [InlineData(DataKind.Text)]
    [InlineData(DataKind.Uuid)]
    [InlineData(DataKind.TimestampTz)]
    public async Task GenerateAsync_ShouldProduceNonNullValueForSemanticKind(DataKind kind)
    {
        var registry = new GeneratorRegistry();
        registry.RegisterDefaults(new DeterministicRandomService(42));

        var context = MakeContext(kind);
        var resolved = registry.TryResolve(kind, null, out var generator);

        resolved.Should().BeTrue($"generator should be registered for {kind}");

        var value = await generator!(context, CancellationToken.None);
        value.Should().NotBeNull($"generator for {kind} should produce a value");
        value!.ToString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task EmailGenerator_ShouldProduceValidEmailPattern()
    {
        var registry = new GeneratorRegistry();
        registry.RegisterDefaults(new DeterministicRandomService(42));
        registry.TryResolve(DataKind.Email, null, out var gen);

        var value = (string)(await gen!(MakeContext(DataKind.Email), CancellationToken.None))!;
        value.Should().Contain("@");
        value.Should().Contain(".");
    }

    [Fact]
    public async Task UuidGenerator_ShouldProduceValidGuid()
    {
        var registry = new GeneratorRegistry();
        registry.RegisterDefaults(new DeterministicRandomService(42));
        registry.TryResolve(DataKind.Uuid, null, out var gen);

        var value = (string)(await gen!(MakeContext(DataKind.Uuid), CancellationToken.None))!;
        Guid.TryParse(value, out _).Should().BeTrue();
    }

    [Fact]
    public async Task IntegerGenerator_WithMinMax_RespectsRange()
    {
        var registry = new GeneratorRegistry();
        registry.RegisterDefaults(new DeterministicRandomService(42));
        registry.TryResolve(DataKind.Integer, null, out var gen);

        var rules = new RuleConfiguration
        {
            Version = "1",
            Tables = [new TableRule { Table = "table", Columns = [new ColumnRule { Column = "col", MinValue = "100", MaxValue = "200" }] }]
        };
        var ctx = new GeneratorContext("table", "col", DataKind.Integer, 0, rules,
            new Dictionary<string, object?>(),
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>());

        for (var i = 0; i < 20; i++)
        {
            var row = ctx with { RowIndex = i };
            var value = (int)(await gen!(row, CancellationToken.None))!;
            value.Should().BeInRange(100, 200);
        }
    }

    [Fact]
    public async Task DecimalGenerator_WithMinMax_RespectsRange()
    {
        var registry = new GeneratorRegistry();
        registry.RegisterDefaults(new DeterministicRandomService(42));
        registry.TryResolve(DataKind.Decimal, null, out var gen);

        var rules = new RuleConfiguration
        {
            Version = "1",
            Tables = [new TableRule { Table = "t", Columns = [new ColumnRule { Column = "price", MinValue = "5.0", MaxValue = "10.0" }] }]
        };
        var ctx = new GeneratorContext("t", "price", DataKind.Decimal, 0, rules,
            new Dictionary<string, object?>(),
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>());

        for (var i = 0; i < 20; i++)
        {
            var row = ctx with { RowIndex = i };
            var value = (decimal)(await gen!(row, CancellationToken.None))!;
            value.Should().BeInRange(5.0m, 10.0m);
        }
    }

    [Fact]
    public async Task StringGenerator_WithPattern_MatchesPattern()
    {
        var registry = new GeneratorRegistry();
        registry.RegisterDefaults(new DeterministicRandomService(42));
        registry.TryResolve(DataKind.String, null, out var gen);

        var rules = new RuleConfiguration
        {
            Version = "1",
            Tables = [new TableRule { Table = "t", Columns = [new ColumnRule { Column = "code", Pattern = "[A-Z]{3}[0-9]{4}" }] }]
        };
        var ctx = new GeneratorContext("t", "code", DataKind.String, 0, rules,
            new Dictionary<string, object?>(),
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>());

        for (var i = 0; i < 5; i++)
        {
            var row = ctx with { RowIndex = i };
            var value = (string)(await gen!(row, CancellationToken.None))!;
            value.Should().MatchRegex(@"^[A-Z]{3}[0-9]{4}$");
        }
    }

    [Fact]
    public async Task DateGenerator_WithMinMax_RespectsRange()
    {
        var registry = new GeneratorRegistry();
        registry.RegisterDefaults(new DeterministicRandomService(42));
        registry.TryResolve(DataKind.Date, null, out var gen);

        var rules = new RuleConfiguration
        {
            Version = "1",
            Tables = [new TableRule { Table = "t", Columns = [new ColumnRule { Column = "dob", MinValue = "2000-01-01", MaxValue = "2010-12-31" }] }]
        };
        var ctx = new GeneratorContext("t", "dob", DataKind.Date, 0, rules,
            new Dictionary<string, object?>(),
            new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>());

        var minDate = DateOnly.Parse("2000-01-01");
        var maxDate = DateOnly.Parse("2010-12-31");

        for (var i = 0; i < 20; i++)
        {
            var row = ctx with { RowIndex = i };
            var value = (DateOnly)(await gen!(row, CancellationToken.None))!;
            value.Should().BeOnOrAfter(minDate).And.BeOnOrBefore(maxDate);
        }
    }
}
