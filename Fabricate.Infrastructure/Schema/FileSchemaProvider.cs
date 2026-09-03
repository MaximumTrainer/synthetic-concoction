using System.Text.Json;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Schema;

/// <summary>Schema provider that reads a pre-exported <see cref="DatabaseSchema"/> JSON file.
/// Useful for CI/offline workflows where a live database is unavailable.</summary>
public sealed class FileSchemaProvider(string filePath, string databaseName) : ISchemaProvider
{
    public string ProviderName => "file";

    public async Task<DatabaseSchema> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new SchemaProviderException(ProviderName, $"Schema file not found: '{filePath}'.");
        }

        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var schema = await JsonSerializer.DeserializeAsync<DatabaseSchema>(stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken).ConfigureAwait(false);

            if (schema is null)
            {
                throw new SchemaProviderException(ProviderName, $"Schema file '{filePath}' deserialized to null.");
            }

            return schema with { Name = databaseName };
        }
        catch (JsonException ex)
        {
            throw new SchemaProviderException(ProviderName, $"Invalid JSON in schema file '{filePath}'.", ex);
        }
    }
}
