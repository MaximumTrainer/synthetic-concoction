using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Workspaces;

/// <summary>
/// Workspace connections and their secrets (#69). Connections previously carried a name and a provider but no
/// connection string, so "connections can be validated" and "secrets are never exposed" had nothing to act on.
///
/// <para>
/// The connection string is written once, encrypted with the same cipher as LLM credentials, and never returned:
/// every read produces a <see cref="ConnectionSummary"/> carrying a fingerprint and a redacted form instead.
/// </para>
/// </summary>
public sealed class ConnectionCatalogService(
    IConnectionRepository connectionRepository,
    IWorkspaceService workspaceService,
    ISecretCipher cipher,
    ISchemaProviderFactory providerFactory,
    IAuditLogService auditLogService) : IConnectionCatalogService
{
    public async Task<ConnectionSummary> AddConnectionAsync(
        Guid workspaceId,
        string name,
        string provider,
        Guid requestingUserId,
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        var workspace = await RequireEditorAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        RequireSupported(provider);

        var connection = WithSecret(
            new Connection(Guid.NewGuid(), workspaceId, name, provider, "active", DateTimeOffset.UtcNow),
            connectionString);

        var saved = await connectionRepository.SaveAsync(connection, cancellationToken).ConfigureAwait(false);
        await AuditAsync(workspace, requestingUserId, "connection.created", saved, cancellationToken).ConfigureAwait(false);
        return saved.ToSummary();
    }

    public async Task<ConnectionSummary> RotateAsync(
        Guid workspaceId,
        Guid connectionId,
        string connectionString,
        Guid requestingUserId,
        CancellationToken cancellationToken = default)
    {
        var workspace = await RequireEditorAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        var existing = await GetOwnedOrThrowAsync(workspaceId, connectionId, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A connection string is required.", nameof(connectionString));
        }

        var rotated = WithSecret(existing with { LastValidatedAt = null, LastValidationError = null }, connectionString);
        var saved = await connectionRepository.SaveAsync(rotated, cancellationToken).ConfigureAwait(false);

        await AuditAsync(workspace, requestingUserId, "connection.rotated", saved, cancellationToken).ConfigureAwait(false);
        return saved.ToSummary();
    }

    public async Task<ConnectionValidationResult> ValidateAsync(
        Guid workspaceId,
        Guid connectionId,
        Guid requestingUserId,
        CancellationToken cancellationToken = default)
    {
        var workspace = await RequireEditorAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        var connection = await GetOwnedOrThrowAsync(workspaceId, connectionId, cancellationToken).ConfigureAwait(false);

        if (!connection.HasSecret)
        {
            return new ConnectionValidationResult(connectionId, false, "No connection string has been stored.", DateTimeOffset.UtcNow);
        }

        var secret = cipher.Decrypt(connection.CipherText, connection.KeyVersion);
        string message;
        bool reachable;

        try
        {
            // Discovery is the cheapest honest liveness check: it opens the connection and reads metadata, which
            // is what the connection is for. A driver-level ping can succeed where the credentials cannot read.
            await providerFactory.Create(connection.Provider, secret).DiscoverAsync(cancellationToken).ConfigureAwait(false);
            reachable = true;
            message = "Reachable.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            reachable = false;
            // Drivers quote the connection string back in their messages more often than not.
            message = ConnectionStringRedactor.Scrub(ex.Message, secret);
        }

        var checkedAt = DateTimeOffset.UtcNow;
        var updated = connection with
        {
            LastValidatedAt = checkedAt,
            LastValidationError = reachable ? null : message,
            Status = reachable ? "active" : "unreachable",
        };

        await connectionRepository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        await AuditAsync(workspace, requestingUserId, reachable ? "connection.validated" : "connection.validation_failed", updated, cancellationToken).ConfigureAwait(false);

        return new ConnectionValidationResult(connectionId, reachable, message, checkedAt);
    }

    public async Task<ConnectionSummary> UpdateStatusAsync(Guid connectionId, string status, Guid requestingUserId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var workspace = await RequireEditorAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        var existing = await GetOwnedOrThrowAsync(workspaceId, connectionId, cancellationToken).ConfigureAwait(false);

        var updated = existing with
        {
            Status = status,
            DisabledAt = status == "disabled" ? DateTimeOffset.UtcNow : null,
        };

        var saved = await connectionRepository.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        await AuditAsync(workspace, requestingUserId, "connection.status_changed", saved, cancellationToken).ConfigureAwait(false);
        return saved.ToSummary();
    }

    public async Task RemoveConnectionAsync(Guid connectionId, Guid requestingUserId, Guid workspaceId, CancellationToken cancellationToken = default)
    {
        var workspace = await RequireEditorAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);

        var existing = await connectionRepository.GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.WorkspaceId != workspaceId) return;

        await connectionRepository.DeleteAsync(connectionId, cancellationToken).ConfigureAwait(false);
        await AuditAsync(workspace, requestingUserId, "connection.deleted", existing, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConnectionSummary>> ListAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        await RequireMemberAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        var connections = await connectionRepository.ListByWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        return connections.Select(c => c.ToSummary()).ToArray();
    }

    public async Task<ConnectionSummary?> GetAsync(Guid workspaceId, Guid connectionId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var role = await workspaceService.GetEffectiveRoleAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false);
        if (role is null) return null;

        var connection = await connectionRepository.GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(false);
        return connection?.WorkspaceId == workspaceId ? connection.ToSummary() : null;
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private Connection WithSecret(Connection connection, string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return connection;

        var (cipherText, keyVersion) = cipher.Encrypt(connectionString);
        return connection with
        {
            CipherText = cipherText,
            KeyVersion = keyVersion,
            Fingerprint = ConnectionStringRedactor.Fingerprint(connectionString),
            Redacted = ConnectionStringRedactor.Redact(connectionString),
        };
    }

    private void RequireSupported(string provider)
    {
        if (!providerFactory.SupportedProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Unsupported connection provider '{provider}'. Supported: {string.Join(", ", providerFactory.SupportedProviders)}.",
                nameof(provider));
        }
    }

    private async Task<Connection> GetOwnedOrThrowAsync(Guid workspaceId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await connectionRepository.GetByIdAsync(connectionId, cancellationToken).ConfigureAwait(false);

        // Not-found rather than forbidden for another workspace's id: a 403 would confirm it exists.
        return connection is not null && connection.WorkspaceId == workspaceId
            ? connection
            : throw new KeyNotFoundException($"Connection '{connectionId}' was not found in this workspace.");
    }

    private async Task<Workspace> RequireEditorAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        var role = await workspaceService.GetEffectiveRoleAsync(workspaceId, userId, cancellationToken).ConfigureAwait(false);
        if (role is null or < WorkspaceRole.Editor)
        {
            throw new UnauthorizedAccessException("Workspace Editor or Admin role required.");
        }

        return await workspaceService.GetByIdAsync(workspaceId, userId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Workspace '{workspaceId}' was not found.");
    }

    private async Task RequireMemberAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        if (await workspaceService.GetEffectiveRoleAsync(workspaceId, userId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new UnauthorizedAccessException("Access denied to workspace.");
        }
    }

    private Task AuditAsync(Workspace workspace, Guid actorUserId, string action, Connection connection, CancellationToken cancellationToken)
        => auditLogService.RecordAsync(
            new AuditEvent(
                Guid.NewGuid(),
                workspace.AccountId,
                actorUserId,
                action,
                "Connection",
                connection.Id.ToString(),
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                // The redacted form and the fingerprint, never the connection string itself.
                $"workspace={connection.WorkspaceId};provider={connection.Provider};fingerprint={connection.Fingerprint};target={connection.Redacted}"),
            cancellationToken);
}
