using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Workspaces;

/// <summary>
/// Picks the database a chat session should introspect (#69).
///
/// <para>
/// Precedence: the session's project database, if it names a workspace connection; then the workspace's own
/// single active connection. Null means the workspace has none, and the caller falls back to the instance-level
/// provider — which keeps the CLI and single-tenant self-hosting working exactly as before.
/// </para>
///
/// <para>
/// The connection string is decrypted here and handed straight to a provider built for this call. It is never
/// stored on the resolver, never cached, and never returned.
/// </para>
/// </summary>
public sealed class ConnectionResolver(
    IConnectionRepository connectionRepository,
    IProjectDatabaseRepository projectDatabaseRepository,
    ISecretCipher cipher,
    ISchemaProviderFactory providerFactory) : IConnectionResolver
{
    public async Task<ISchemaProvider?> ResolveAsync(Guid workspaceId, Guid? projectId, CancellationToken cancellationToken = default)
    {
        var connection = await ResolveConnectionAsync(workspaceId, projectId, cancellationToken).ConfigureAwait(false);
        if (connection is null || !connection.HasSecret || !connection.IsActive) return null;

        var secret = cipher.Decrypt(connection.CipherText, connection.KeyVersion);
        return providerFactory.Create(connection.Provider, secret);
    }

    private async Task<Connection?> ResolveConnectionAsync(Guid workspaceId, Guid? projectId, CancellationToken cancellationToken)
    {
        var candidates = await connectionRepository.ListByWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var usable = candidates.Where(c => c.IsActive && c.HasSecret).ToArray();
        if (usable.Length == 0) return null;

        if (projectId is Guid project)
        {
            var databases = await projectDatabaseRepository.ListByProjectAsync(project, cancellationToken).ConfigureAwait(false);
            var external = databases
                .Where(d => d.Type == ProjectDatabaseType.External && d.ConnectionRefId is not null)
                .Select(d => usable.FirstOrDefault(c => c.Id == d.ConnectionRefId))
                .FirstOrDefault(c => c is not null);

            if (external is not null) return external;
        }

        // Only when the workspace has exactly one. With several and no project binding, guessing which of a
        // customer's databases to introspect is worse than falling back to the configured default.
        return usable.Length == 1 ? usable[0] : null;
    }
}
