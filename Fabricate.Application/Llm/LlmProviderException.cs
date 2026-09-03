namespace Fabricate.Application.Llm;

public enum LlmFailureKind
{
    Transport = 0,
    Authentication,
    RateLimited,
    ContextLengthExceeded,
    Timeout,
    InvalidRequest,
    ProviderError
}

/// <summary>
/// Provider failure translated into a vendor-neutral shape. Adapters must construct these with messages
/// that are safe to persist and display — never containing the credential or raw request bodies.
/// </summary>
public sealed class LlmProviderException(LlmFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public LlmFailureKind Kind { get; } = kind;

    public bool IsRetryable => Kind is LlmFailureKind.Transport or LlmFailureKind.RateLimited or LlmFailureKind.Timeout or LlmFailureKind.ProviderError;
}
