using Fabricate.Application.Abstractions;
using Fabricate.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Fabricate.Infrastructure.Schema;

/// <summary>
/// Builds a schema provider per connection (#69). Discovery previously resolved a single instance-level
/// <see cref="ISchemaProvider"/>, so every chat session introspected the operator's own database whatever
/// workspace it belonged to.
/// </summary>
public sealed class SchemaProviderFactory : ISchemaProviderFactory
{
    public IReadOnlyList<string> SupportedProviders { get; } = ["sqlite", "postgres", "postgresql"];

    public ISchemaProvider Create(string provider, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // The providers take their connection string through IOptions, so one is built per call rather than
        // resolved from the container — the container's instance is the operator's own database.
        var options = Options.Create(new SchemaProviderOptions
        {
            Provider = provider,
            ConnectionString = connectionString,
        });

        return provider.ToLowerInvariant() switch
        {
            "sqlite" => new SqliteSchemaProvider(options),
            "postgres" or "postgresql" => new PostgreSqlSchemaProvider(options),
            _ => throw new NotSupportedException(
                $"Unsupported connection provider '{provider}'. Supported: {string.Join(", ", SupportedProviders)}."),
        };
    }
}
