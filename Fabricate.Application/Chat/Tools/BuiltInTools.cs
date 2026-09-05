using System.Text.Json;
using System.Text.Json.Serialization;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Chat.Tools;

/// <summary>
/// Built-in tool that discovers the schema from the session's own connection and returns a compact JSON summary
/// suitable for an agent response.
///
/// <para>
/// Until #69 this always used the instance-level provider, so a session in any workspace introspected the
/// operator's own database. It now resolves the session's project database or workspace connection first, and
/// falls back to the configured provider only when the workspace has none — which is what keeps single-tenant
/// self-hosting and the CLI working unchanged.
/// </para>
/// </summary>
public sealed class DiscoverSchemaTool(
    ISchemaDiscoveryService discovery,
    SessionSchemaProviderResolver? resolveForSession = null) : ITool
{
    public string Name => "discover_schema";
    public string Description => "Discover the database schema (tables, columns, FK relationships) for the active connection.";

    public async Task<string> ExecuteAsync(
        string inputJson,
        Guid sessionId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var provider = resolveForSession is null
            ? null
            : await resolveForSession(sessionId, cancellationToken).ConfigureAwait(false);

        var schema = provider is null
            ? await discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false)
            : await provider.DiscoverAsync(cancellationToken).ConfigureAwait(false);

        var summary = new
        {
            tableCount = schema.Tables.Count,
            tables = schema.Tables.Select(t => new
            {
                name = t.QualifiedName,
                columns = t.Columns.Select(c => new { c.Name, kind = c.DataKind.ToString() }),
                foreignKeys = t.ForeignKeys.Select(fk => new
                {
                    fk.Name,
                    referencedTable = fk.ReferencedTable
                })
            })
        };

        return JsonSerializer.Serialize(summary, JsonOptions.Web);
    }
}

/// <summary>
/// Built-in tool that triggers a synthetic data generation run using the provided parameters.
/// Returns a compact run summary for the agent.
/// </summary>
public sealed class GenerateDataTool(ISyntheticDataOrchestrator orchestrator) : ITool
{
    public string Name => "generate_data";
    public string Description => "Generate synthetic data for the active schema. Accepts optional row counts and rule configuration.";

    public string InputSchemaJson => """
        {
          "type": "object",
          "properties": {
            "rowCounts": {
              "type": "object",
              "description": "Rows to generate per table, keyed by qualified table name. Defaults to 10 per table.",
              "additionalProperties": { "type": "integer", "minimum": 0 }
            },
            "seed": { "type": "integer", "description": "Deterministic seed; the same seed and schema always produce the same data. Defaults to 42." }
          },
          "additionalProperties": false
        }
        """;

    private static readonly JsonSerializerOptions _readOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> ExecuteAsync(
        string inputJson,
        Guid sessionId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        GenerateDataInput? input = null;
        if (!string.IsNullOrWhiteSpace(inputJson))
        {
            try { input = JsonSerializer.Deserialize<GenerateDataInput>(inputJson, _readOptions); }
            catch { /* ignore parse errors, use defaults */ }
        }

        var schema = await orchestrator.DiscoverAsync(cancellationToken).ConfigureAwait(false);

        var rowCounts = input?.RowCounts is { Count: > 0 }
            ? input.RowCounts
            : schema.Tables.ToDictionary(static t => t.QualifiedName, _ => 10, StringComparer.Ordinal);

        var seed = input?.Seed ?? 42L;
        var request = new GenerationRequest(schema, rowCounts, seed);
        var (result, summary) = await orchestrator.GenerateAsync(request, cancellationToken).ConfigureAwait(false);

        var response = new
        {
            success = result.IsSuccess,
            summary.TableCount,
            summary.RowCount,
            validationIssueCount = result.ValidationIssues.Count,
            issues = result.ValidationIssues.Select(i => new { i.Table, i.Column, i.Reason }),
            diagnostics = summary.Messages
        };

        return JsonSerializer.Serialize(response, JsonOptions.Web);
    }

    private sealed record GenerateDataInput(
        [property: JsonPropertyName("rowCounts")] IReadOnlyDictionary<string, int>? RowCounts,
        [property: JsonPropertyName("seed")] long? Seed);
}

file static class JsonOptions
{
    internal static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
