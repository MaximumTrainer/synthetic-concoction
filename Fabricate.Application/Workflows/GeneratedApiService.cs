using System.Text.Json;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Workflows;

/// <summary>
/// Ingests OpenAPI contracts, binds their endpoints to generated data, and serves them (#70).
///
/// <para>
/// <c>OpenApiContractIngestionService</c> parsed a document into endpoints that nothing stored and nothing
/// served, so "generated endpoints return contract-valid payloads from generated datasets" was unmet.
/// </para>
/// </summary>
public sealed class GeneratedApiService(
    IApiContractIngestionService ingestion,
    IApiContractRepository repository,
    IWorkspaceService workspaces,
    IRunRepository runs,
    IArtifactStore artifacts) : IGeneratedApiService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ApiContract> IngestAsync(IngestContractCommand command, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await RequireRoleAsync(command.WorkspaceId, requestingUserId, WorkspaceRole.Editor, cancellationToken).ConfigureAwait(false);

        var endpoints = await ingestion
            .IngestAsync(command.DocumentJson, command.WorkspaceId, requestingUserId, cancellationToken)
            .ConfigureAwait(false);

        var contract = new ApiContract(
            Guid.NewGuid(), command.WorkspaceId, command.Name,
            VersionOf(command.DocumentJson), command.DocumentJson, requestingUserId, DateTimeOffset.UtcNow);

        await repository.SaveAsync(contract, cancellationToken).ConfigureAwait(false);

        foreach (var endpoint in endpoints)
        {
            await repository.SaveEndpointAsync(endpoint with { ContractId = contract.Id }, cancellationToken).ConfigureAwait(false);
        }

        return contract;
    }

    public async Task<IReadOnlyList<ApiContract>> ListContractsAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        if (!await HasAccessAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false)) return [];
        return await repository.ListByWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GeneratedApiEndpoint>> ListEndpointsAsync(Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        if (!await HasAccessAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false)) return [];
        return await repository.ListEndpointsAsync(workspaceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GeneratedApiEndpoint?> BindEndpointAsync(
        Guid workspaceId,
        Guid endpointId,
        BindEndpointCommand command,
        Guid requestingUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await RequireRoleAsync(workspaceId, requestingUserId, WorkspaceRole.Editor, cancellationToken).ConfigureAwait(false);

        var endpoint = await repository.GetEndpointAsync(endpointId, cancellationToken).ConfigureAwait(false);
        if (endpoint is null || endpoint.WorkspaceId != workspaceId) return null;

        var updated = endpoint with
        {
            ArtifactRunId = command.ClearBinding ? null : command.ArtifactRunId ?? endpoint.ArtifactRunId,
            BoundTable = command.ClearBinding ? null : command.BoundTable ?? endpoint.BoundTable,
            IsActive = command.IsActive ?? endpoint.IsActive,
            Diagnostics = null,
        };

        if (updated.ArtifactRunId is not null && !string.IsNullOrWhiteSpace(updated.BoundTable))
        {
            // Checked here rather than at request time: a mismatch is a fact about the binding, and finding out
            // when someone's client rejects the payload is the worst moment to learn it.
            updated = updated with { Diagnostics = await ValidateBindingAsync(updated, cancellationToken).ConfigureAwait(false) };
        }

        return await repository.SaveEndpointAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GeneratedApiResponse?> ServeAsync(
        Guid workspaceId,
        string method,
        string path,
        Guid requestingUserId,
        CancellationToken cancellationToken = default)
    {
        if (!await HasAccessAsync(workspaceId, requestingUserId, cancellationToken).ConfigureAwait(false)) return null;

        var endpoints = await repository.ListEndpointsAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var match = Match(endpoints, method, path);
        if (match is null) return null;

        var (endpoint, pathValues) = match.Value;
        if (!endpoint.IsServable) return null;

        var rows = await LoadRowsAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (rows is null) return null;

        if (endpoint.ResponseKind == GeneratedResponseKind.Collection)
        {
            return new GeneratedApiResponse(JsonSerializer.Serialize(rows, Json), endpoint.OperationId, endpoint.Id);
        }

        // An item operation matches on the trailing path parameter, compared as text so it works whether the
        // key is an integer, a GUID or a string.
        var wanted = pathValues.Count > 0 ? pathValues[^1] : null;
        var row = wanted is null
            ? rows.FirstOrDefault()
            : rows.FirstOrDefault(r => r.Values.Any(v => string.Equals(v?.ToString(), wanted, StringComparison.OrdinalIgnoreCase)));

        return row is null
            ? new GeneratedApiResponse("""{"error":"Not found"}""", endpoint.OperationId, endpoint.Id, StatusCode: 404)
            : new GeneratedApiResponse(JsonSerializer.Serialize(row, Json), endpoint.OperationId, endpoint.Id);
    }

    // ── matching ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The endpoint whose method and path template match, with the values the template's parameters captured.
    /// Literal segments are preferred over parameters, so <c>/customers/count</c> beats <c>/customers/{id}</c>.
    /// </summary>
    private static (GeneratedApiEndpoint Endpoint, IReadOnlyList<string> PathValues)? Match(
        IReadOnlyList<GeneratedApiEndpoint> endpoints,
        string method,
        string path)
    {
        var requested = Segments(path);
        (GeneratedApiEndpoint Endpoint, IReadOnlyList<string> Values, int Literals)? best = null;

        foreach (var endpoint in endpoints)
        {
            if (!string.Equals(endpoint.Method, method, StringComparison.OrdinalIgnoreCase)) continue;

            var template = Segments(endpoint.Path);
            if (template.Length != requested.Length) continue;

            var values = new List<string>();
            var literals = 0;
            var matched = true;

            for (var i = 0; i < template.Length; i++)
            {
                if (template[i].StartsWith('{') && template[i].EndsWith('}'))
                {
                    values.Add(requested[i]);
                }
                else if (string.Equals(template[i], requested[i], StringComparison.OrdinalIgnoreCase))
                {
                    literals++;
                }
                else
                {
                    matched = false;
                    break;
                }
            }

            if (matched && (best is null || literals > best.Value.Literals))
            {
                best = (endpoint, values, literals);
            }
        }

        return best is null ? null : (best.Value.Endpoint, best.Value.Values);
    }

    private static string[] Segments(string path)
        => path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    // ── data ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The bound table's rows, read from the run's JSON artifact. Null when the run, the artifact or the table
    /// is gone — including when retention purged it (#84), which is a 404 rather than a 500.
    /// </summary>
    private async Task<List<Dictionary<string, object?>>?> LoadRowsAsync(GeneratedApiEndpoint endpoint, CancellationToken cancellationToken)
    {
        var run = await runs.GetByIdAsync(endpoint.ArtifactRunId!.Value, cancellationToken).ConfigureAwait(false);
        if (run is null || run.WorkspaceId != endpoint.WorkspaceId) return null;

        var name = $"json/{endpoint.BoundTable!.Replace('.', '_')}.json";
        var stored = await artifacts.ListAsync(run.Id.ToString(), cancellationToken).ConfigureAwait(false);
        var artifact = stored.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (artifact is null) return null;

        await using var content = await artifacts.RetrieveAsync(artifact.Path, cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<List<Dictionary<string, object?>>>(content, Json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks the bound rows against the contract's response schema. Returns the diagnostic, or null when the
    /// binding is sound.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: required properties present, and declared primitive types not contradicted. That
    /// catches the mistake this is for — binding an endpoint to the wrong table — without pulling in a full JSON
    /// Schema implementation whose failures would be harder to act on than the mismatch itself.
    /// </remarks>
    private async Task<string?> ValidateBindingAsync(GeneratedApiEndpoint endpoint, CancellationToken cancellationToken)
    {
        var rows = await LoadRowsAsync(endpoint, cancellationToken).ConfigureAwait(false);
        if (rows is null)
        {
            return $"No artifact for table '{endpoint.BoundTable}' in run {endpoint.ArtifactRunId}. " +
                   "Bind to a completed run exported with the json format.";
        }

        if (string.IsNullOrWhiteSpace(endpoint.ResponseSchemaJson)) return null;
        if (rows.Count == 0) return null;

        using var schema = JsonDocument.Parse(endpoint.ResponseSchemaJson);
        var root = schema.RootElement;
        var sample = rows[0];
        var problems = new List<string>();

        if (root.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            var missing = required.EnumerateArray()
                .Select(e => e.GetString())
                .Where(name => name is not null && !sample.ContainsKey(name))
                .ToArray();

            if (missing.Length > 0)
            {
                problems.Add($"required propert{(missing.Length == 1 ? "y" : "ies")} {string.Join(", ", missing)} " +
                             $"missing from table '{endpoint.BoundTable}'");
            }
        }

        if (root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (!sample.TryGetValue(property.Name, out var value) || value is null) continue;

                var declared = property.Value.GetString();
                if (!TypeMatches(declared, value))
                {
                    problems.Add($"'{property.Name}' is declared {declared} but the data has {Describe(value)}");
                }
            }
        }

        return problems.Count == 0 ? null : "Contract mismatch: " + string.Join("; ", problems) + ".";
    }

    private static bool TypeMatches(string? declared, object? value) => declared switch
    {
        null or "any" => true,
        "string" => Describe(value) is "string",
        "integer" or "number" => Describe(value) is "number",
        "boolean" => Describe(value) is "boolean",
        "array" => Describe(value) is "array",
        "object" => Describe(value) is "object",
        _ => true,
    };

    private static string Describe(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.String } => "string",
        JsonElement { ValueKind: JsonValueKind.Number } => "number",
        JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False } => "boolean",
        JsonElement { ValueKind: JsonValueKind.Array } => "array",
        JsonElement { ValueKind: JsonValueKind.Object } => "object",
        JsonElement { ValueKind: JsonValueKind.Null } => "null",
        string => "string",
        bool => "boolean",
        null => "null",
        _ => "number",
    };

    private static string VersionOf(string documentJson)
    {
        try
        {
            using var document = JsonDocument.Parse(documentJson);
            return document.RootElement.TryGetProperty("info", out var info)
                && info.TryGetProperty("version", out var version)
                ? version.GetString() ?? "unknown"
                : "unknown";
        }
        catch (JsonException)
        {
            return "unknown";
        }
    }

    private async Task<bool> HasAccessAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
        => await workspaces.GetEffectiveRoleAsync(workspaceId, userId, cancellationToken).ConfigureAwait(false) is not null;

    private async Task RequireRoleAsync(Guid workspaceId, Guid userId, WorkspaceRole minimum, CancellationToken cancellationToken)
    {
        var role = await workspaces.GetEffectiveRoleAsync(workspaceId, userId, cancellationToken).ConfigureAwait(false);
        if (role is null || role < minimum)
        {
            throw new UnauthorizedAccessException($"Workspace {minimum} role or above is required.");
        }
    }
}
