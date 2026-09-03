namespace Fabricate.Infrastructure.Schema;

public sealed class SchemaProviderException(string provider, string message, Exception? innerException = null)
    : Exception($"[{provider}] {message}", innerException);
