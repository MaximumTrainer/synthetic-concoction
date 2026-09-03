using Anthropic;
using Anthropic.Bedrock;
using Anthropic.Foundry;
using Anthropic.Vertex;
using Fabricate.Application.Abstractions;
using Fabricate.Application.Llm;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Llm;

/// <summary>
/// The one place vendor SDK clients are constructed. A client is built per resolved credential and never cached,
/// so a rotated or revoked credential takes effect on the next turn.
/// </summary>
public sealed class ChatCompletionClientFactory(IHttpClientFactory httpClientFactory, LlmOptions options) : IChatCompletionClientFactory
{
    public const string HttpClientName = "fabricate-llm";

    public IChatCompletionClient Create(ResolvedLlmCredential credential)
    {
        var timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        switch (credential.Provider)
        {
            case LlmProvider.Anthropic:
            {
                var client = string.IsNullOrWhiteSpace(credential.Endpoint)
                    ? new AnthropicClient { ApiKey = credential.GetSecret(), Timeout = timeout, MaxRetries = 2 }
                    : new AnthropicClient { ApiKey = credential.GetSecret(), Timeout = timeout, MaxRetries = 2, BaseUrl = credential.Endpoint };
                return new AnthropicChatCompletionClient(client, "anthropic");
            }

            case LlmProvider.AwsBedrock:
            {
                var region = credential.GetSetting("region") ?? options.Region
                    ?? throw new LlmProviderException(LlmFailureKind.InvalidRequest, "Bedrock credentials require a 'region' setting.");
                var client = new AnthropicBedrockMantleClient(new MantleAwsClientOptions { AwsRegion = region })
                {
                    Timeout = timeout,
                    MaxRetries = 2,
                };
                return new AnthropicChatCompletionClient(client, "bedrock");
            }

            case LlmProvider.GcpVertexAi:
            {
                var projectId = credential.GetSetting("projectId") ?? options.ProjectId
                    ?? throw new LlmProviderException(LlmFailureKind.InvalidRequest, "Vertex credentials require a 'projectId' setting.");
                var location = credential.GetSetting("location") ?? options.Location ?? "global";
                var client = new AnthropicVertexClient(new AnthropicVertexCredentials(projectId, location))
                {
                    Timeout = timeout,
                    MaxRetries = 2,
                };
                return new AnthropicChatCompletionClient(client, "vertex");
            }

            case LlmProvider.AzureFoundry:
            {
                var resource = credential.GetSetting("resourceName")
                    ?? ResourceNameFromEndpoint(credential.Endpoint ?? options.BaseUrl)
                    ?? throw new LlmProviderException(LlmFailureKind.InvalidRequest, "Foundry credentials require a 'resourceName' setting or an endpoint.");
                var client = new AnthropicFoundryClient(new AnthropicFoundryApiKeyCredentials(resource, credential.GetSecret()))
                {
                    Timeout = timeout,
                    MaxRetries = 2,
                };
                return new AnthropicChatCompletionClient(client, "foundry");
            }

            case LlmProvider.OpenAiCompatible:
            {
                var baseUrl = credential.Endpoint ?? options.BaseUrl
                    ?? throw new LlmProviderException(LlmFailureKind.InvalidRequest, "OpenAI-compatible credentials require an endpoint.");
                var http = httpClientFactory.CreateClient(HttpClientName);
                http.Timeout = timeout;
                return new OpenAiCompatibleChatCompletionClient(http, baseUrl, credential.GetSecret());
            }

            default:
                throw new LlmProviderException(LlmFailureKind.InvalidRequest, $"Provider '{credential.Provider}' is not supported.");
        }
    }

    private static string? ResourceNameFromEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return null;
        // https://<resource>.services.ai.azure.com/... → <resource>
        var host = uri.Host;
        var dot = host.IndexOf('.');
        return dot > 0 ? host[..dot] : host;
    }
}
