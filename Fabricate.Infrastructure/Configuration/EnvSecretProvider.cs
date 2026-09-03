using Fabricate.Application.Abstractions;

namespace Fabricate.Infrastructure.Configuration;

/// <summary>Resolves secrets from environment variables. Never logs or exposes retrieved values.</summary>
public sealed class EnvSecretProvider : ISecretProvider
{
    public Task<string> ResolveAsync(string secretName, CancellationToken cancellationToken = default)
    {
        var value = Environment.GetEnvironmentVariable(secretName);
        if (value is null)
        {
            throw new InvalidOperationException($"Secret '{secretName}' not found in environment variables.");
        }

        return Task.FromResult(value);
    }

    public Task<bool> ExistsAsync(string secretName, CancellationToken cancellationToken = default)
        => Task.FromResult(Environment.GetEnvironmentVariable(secretName) is not null);
}
