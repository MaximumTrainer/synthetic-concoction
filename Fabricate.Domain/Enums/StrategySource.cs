namespace Fabricate.Domain.Enums;

/// <summary>Source that determined the sensitive-field strategy applied to a column.</summary>
public enum StrategySource
{
    None = 0,
    Policy,
    Rule,
    User
}
