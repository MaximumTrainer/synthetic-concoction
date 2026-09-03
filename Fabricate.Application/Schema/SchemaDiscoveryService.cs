using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Schema;

public sealed class SchemaDiscoveryService(ISchemaProvider schemaProvider) : ISchemaDiscoveryService
{
    public async Task<DatabaseSchema> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var schema = await schemaProvider.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        return Normalize(schema);
    }

    private static DatabaseSchema Normalize(DatabaseSchema schema)
    {
        var tables = schema.Tables
            .OrderBy(static t => t.QualifiedName, StringComparer.Ordinal)
            .Select(static table => table with
            {
                Columns = table.Columns.OrderBy(static c => c.Name, StringComparer.Ordinal).ToArray(),
                ForeignKeys = table.ForeignKeys.OrderBy(static f => f.Name, StringComparer.Ordinal).ToArray(),
                UniqueConstraints = table.UniqueConstraints.OrderBy(static u => u.Name, StringComparer.Ordinal).ToArray(),
                Indexes = table.Indexes.OrderBy(static i => i.Name, StringComparer.Ordinal).ToArray()
            })
            .ToArray();

        return schema with { Tables = tables };
    }
}
