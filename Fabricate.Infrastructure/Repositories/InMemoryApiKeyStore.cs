using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Repositories;

public sealed class InMemoryApiKeyStore : IApiKeyStore
{
    private readonly List<ApiKey> _keys = [];

    public Task<ApiKey> SaveAsync(ApiKey key, CancellationToken cancellationToken = default)
    {
        _keys.RemoveAll(k => k.Id == key.Id);
        _keys.Add(key);
        return Task.FromResult(key);
    }

    public Task<ApiKey?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_keys.Find(k => k.Id == id));

    public Task<IReadOnlyList<ApiKey>> ListByAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ApiKey>>(_keys.Where(k => k.AccountId == accountId).ToArray());

    public Task<ApiKey?> FindByHashAsync(string hashedSecret, CancellationToken cancellationToken = default)
        => Task.FromResult(_keys.Find(k => string.Equals(k.HashedSecret, hashedSecret, StringComparison.OrdinalIgnoreCase)));

    public Task<ApiKey> UpdateAsync(ApiKey key, CancellationToken cancellationToken = default)
    {
        var index = _keys.FindIndex(k => k.Id == key.Id);
        if (index < 0) throw new InvalidOperationException($"API key '{key.Id}' not found.");
        _keys[index] = key;
        return Task.FromResult(key);
    }
}
