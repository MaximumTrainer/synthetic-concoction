using System.Text.Json;
using System.Text.Json.Serialization;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Application.Chat.Tools;

/// <summary>
/// Built-in tool that discovers the schema from the active workspace connection
/// and returns a compact JSON summary suitable for an agent response.
/// </summary>
public sealed class DiscoverSchemaTool(ISchemaDiscoveryService discovery) : ITool
{
    public string Name => "discover_schema";
    public string Description => "Discover the database schema (tables, columns, FK relationships) for the active connection.";

    public async Task<string> ExecuteAsync(
        string inputJson,
        Guid sessionId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var schema = await discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);

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
