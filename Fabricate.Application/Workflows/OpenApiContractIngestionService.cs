using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Microsoft.OpenApi;

namespace Fabricate.Application.Workflows;

public sealed class OpenApiContractIngestionService : IApiContractIngestionService
{
    public async Task<IReadOnlyList<GeneratedApiEndpoint>> IngestAsync(string openApiJson, Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var result = OpenApiDocument.Parse(openApiJson, "json", settings: null);

        var errors = result.Diagnostic?.Errors;
        if (errors is { Count: > 0 })
        {
            throw new InvalidOperationException(
                $"OpenAPI parse errors: {string.Join("; ", errors.Select(e => e.Message))}");
        }

        var endpoints = new List<GeneratedApiEndpoint>();
        var paths = result.Document?.Paths;

        if (paths is not null)
        {
            foreach (var (path, pathItem) in paths)
            {
                if (pathItem.Operations is null)
                {
                    continue;
                }

                foreach (var (method, operation) in pathItem.Operations)
                {
                    var verb = method.Method.ToUpperInvariant();

                    // An operation whose successful response is an array serves the whole table; anything else
                    // serves one row. Falling back to the path shape covers contracts with no response schema.
                    var responseSchema = SuccessResponseSchema(operation);
                    var kind = responseSchema?.Type == JsonSchemaType.Array
                        || (responseSchema is null && !path.TrimEnd('/').EndsWith('}'))
                        ? GeneratedResponseKind.Collection
                        : GeneratedResponseKind.Item;

                    var endpoint = new GeneratedApiEndpoint(
                        Guid.NewGuid(),
                        workspaceId,
                        path,
                        verb,
                        operation.OperationId ?? $"{verb}_{path.TrimStart('/').Replace('/', '_')}",
                        null,
                        true,
                        DateTimeOffset.UtcNow,
                        ResponseKind: kind,
                        ResponseSchemaJson: SerialiseSchema(responseSchema));

                    endpoints.Add(endpoint);
                }
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return endpoints;
    }

    /// <summary>The schema of the operation's first 2xx JSON response, or null when it declares none.</summary>
    private static IOpenApiSchema? SuccessResponseSchema(OpenApiOperation operation)
    {
        if (operation.Responses is null) return null;

        foreach (var (status, response) in operation.Responses)
        {
            if (!status.StartsWith('2')) continue;

            if (response.Content is not null
                && response.Content.TryGetValue("application/json", out var media)
                && media.Schema is not null)
            {
                return media.Schema;
            }
        }

        return null;
    }

    /// <summary>
    /// The response schema, reduced to what the payload check needs: the item type, its properties and which of
    /// them are required. Kept as JSON so the endpoint row carries it without depending on the OpenAPI types.
    /// </summary>
    private static string? SerialiseSchema(IOpenApiSchema? schema)
    {
        if (schema is null) return null;

        var item = schema.Type == JsonSchemaType.Array ? schema.Items : schema;
        if (item?.Properties is null || item.Properties.Count == 0) return null;

        var properties = item.Properties.ToDictionary(
            p => p.Key,
            p => p.Value.Type?.ToString()?.ToLowerInvariant() ?? "any",
            StringComparer.Ordinal);

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            required = item.Required?.ToArray() ?? [],
            properties,
        });
    }

}
