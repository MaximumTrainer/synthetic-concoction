using Fabricate.Application.Abstractions;

namespace Fabricate.Infrastructure.Schema;

/// <summary>
/// Resolves the registered <see cref="INoSqlSchemaDiscoverer"/> for a given provider name.
/// </summary>
public sealed class NoSqlSchemaDiscovererFactory(IEnumerable<INoSqlSchemaDiscoverer> discoverers) : INoSqlSchemaDiscovererFactory
{
    private readonly IReadOnlyDictionary<string, INoSqlSchemaDiscoverer> _registry =
        discoverers.ToDictionary(static d => d.ProviderName, StringComparer.OrdinalIgnoreCase);

    public INoSqlSchemaDiscoverer GetDiscoverer(string providerName)
    {
        if (_registry.TryGetValue(providerName, out var discoverer))
            return discoverer;

        var known = string.Join(", ", _registry.Keys.OrderBy(static k => k));
        throw new NotSupportedException(
            $"No NoSQL schema discoverer is registered for provider '{providerName}'. " +
            $"Known providers: {known}.");
    }
}
