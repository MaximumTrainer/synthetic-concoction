using Fabricate.Application.Constraints;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;
using FluentAssertions;

namespace Fabricate.Tests.Application;

public sealed class ConstraintEvaluatorTests
{
    [Fact]
    public void Evaluate_ShouldDetectNullAndUniqueViolations()
    {
        var evaluator = new ConstraintEvaluator();
        var table = new TableSchema(
            "main",
            "users",
            [
                new ColumnSchema("id", "int", DataKind.Integer, false, true, true, null, null, null, null),
                new ColumnSchema("email", "text", DataKind.String, false, false, true, 100, null, null, null)
            ],
            ["id"],
            [],
            [new UniqueConstraintSchema("uq_users_email", ["email"])],
            []);

        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["email"] = "a@example.com" },
            new Dictionary<string, object?> { ["id"] = 2, ["email"] = "a@example.com" },
            new Dictionary<string, object?> { ["id"] = null, ["email"] = "b@example.com" }
        };

        var issues = evaluator.Evaluate(table, rows);

        issues.Should().NotBeEmpty();
        issues.Should().Contain(i => i.Column == "id");
        issues.Should().Contain(i => i.Column == "email");
    }
}
