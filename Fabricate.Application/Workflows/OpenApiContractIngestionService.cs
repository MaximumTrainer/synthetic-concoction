using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

namespace Fabricate.Application.Workflows;

public sealed class OpenApiContractIngestionService : IApiContractIngestionService
{
    public async Task<IReadOnlyList<GeneratedApiEndpoint>> IngestAsync(string openApiJson, Guid workspaceId, Guid requestingUserId, CancellationToken cancellationToken = default)
    {
        var reader = new OpenApiStringReader();
        var document = reader.Read(openApiJson, out var diagnostics);

        if (diagnostics.Errors.Count > 0)
        {
            var errors = string.Join("; ", diagnostics.Errors.Select(e => e.Message));
            throw new InvalidOperationException($"OpenAPI parse errors: {errors}");
        }

        var endpoints = new List<GeneratedApiEndpoint>();

        foreach (var (path, pathItem) in document.Paths)
        {
            foreach (var (method, operation) in pathItem.Operations)
            {
                var endpoint = new GeneratedApiEndpoint(
                    Guid.NewGuid(),
                    workspaceId,
                    path,
                    method.ToString().ToUpperInvariant(),
                    operation.OperationId ?? $"{method}_{path.TrimStart('/').Replace('/', '_')}",
                    null,
                    true,
                    DateTimeOffset.UtcNow);

                endpoints.Add(endpoint);
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return endpoints;
    }
}
