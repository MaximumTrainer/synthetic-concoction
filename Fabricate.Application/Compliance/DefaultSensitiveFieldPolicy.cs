using Fabricate.Application.Abstractions;
using Fabricate.Domain.Enums;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Compliance;

public sealed class DefaultSensitiveFieldPolicy : ISensitiveFieldPolicy
{
    private static readonly (string Pattern, string Classification, SensitiveFieldStrategy Strategy)[] DefaultRules =
    [
        ("email", "PII.Email", SensitiveFieldStrategy.Pseudonymize),
        ("phone", "PII.Phone", SensitiveFieldStrategy.Tokenize),
        ("ssn", "PII.SSN", SensitiveFieldStrategy.Redact),
        ("mrn", "PHI.MRN", SensitiveFieldStrategy.Redact),
        ("dob", "PHI.DOB", SensitiveFieldStrategy.Synthesize)
    ];

    private static readonly (string Pattern, string Classification, SensitiveFieldStrategy Strategy)[] HealthcareRules =
    [
        ("email", "PII.Email", SensitiveFieldStrategy.Pseudonymize),
        ("phone", "PII.Phone", SensitiveFieldStrategy.Tokenize),
        ("ssn", "PII.SSN", SensitiveFieldStrategy.Redact),
        ("mrn", "PHI.MRN", SensitiveFieldStrategy.Redact),
        ("dob", "PHI.DOB", SensitiveFieldStrategy.Redact),
        ("diagnosis", "PHI.Diagnosis", SensitiveFieldStrategy.Synthesize),
        ("medication", "PHI.Medication", SensitiveFieldStrategy.Synthesize),
        ("patient", "PHI.PatientName", SensitiveFieldStrategy.Pseudonymize),
        ("npi", "PHI.NPI", SensitiveFieldStrategy.Tokenize),
        ("dea", "PHI.DEA", SensitiveFieldStrategy.Redact)
    ];

    private static readonly (string Pattern, string Classification, SensitiveFieldStrategy Strategy)[] FinanceRules =
    [
        ("email", "PII.Email", SensitiveFieldStrategy.Pseudonymize),
        ("phone", "PII.Phone", SensitiveFieldStrategy.Tokenize),
        ("ssn", "PII.SSN", SensitiveFieldStrategy.Redact),
        ("account", "PCI.AccountNumber", SensitiveFieldStrategy.Tokenize),
        ("card", "PCI.CardNumber", SensitiveFieldStrategy.Tokenize),
        ("cvv", "PCI.CVV", SensitiveFieldStrategy.Redact),
        ("iban", "PCI.IBAN", SensitiveFieldStrategy.Tokenize),
        ("routing", "PCI.RoutingNumber", SensitiveFieldStrategy.Tokenize),
        ("pin", "PCI.PIN", SensitiveFieldStrategy.Redact),
        ("tax_id", "PII.TaxId", SensitiveFieldStrategy.Redact)
    ];

    public ComplianceDecision Evaluate(string table, ColumnSchema column, ComplianceProfile profile = ComplianceProfile.Default)
    {
        var rules = profile switch
        {
            ComplianceProfile.Healthcare => HealthcareRules,
            ComplianceProfile.Finance => FinanceRules,
            _ => DefaultRules
        };

        foreach (var rule in rules)
        {
            if (column.Name.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase))
            {
                return new ComplianceDecision(table, column.Name, rule.Strategy, rule.Classification, $"Matched pattern '{rule.Pattern}'.", StrategySource.Policy);
            }
        }

        return new ComplianceDecision(table, column.Name, SensitiveFieldStrategy.None, "None", "No sensitive pattern matched.", StrategySource.None);
    }
}
