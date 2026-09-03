namespace Fabricate.Domain.Enums;

public enum SensitiveFieldStrategy
{
    None = 0,
    Redact,
    Pseudonymize,
    Tokenize,
    Synthesize
}
