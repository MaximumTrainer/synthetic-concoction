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

                    var endpoint = new GeneratedApiEndpoint(
                        Guid.NewGuid(),
                        workspaceId,
                        path,
                        verb,
                        operation.OperationId ?? $"{verb}_{path.TrimStart('/').Replace('/', '_')}",
                        null,
                        true,
                        DateTimeOffset.UtcNow);

                    endpoints.Add(endpoint);
                }
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return endpoints;
    }
}
