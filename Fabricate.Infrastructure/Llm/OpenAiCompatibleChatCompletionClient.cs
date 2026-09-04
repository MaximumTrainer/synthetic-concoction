using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fabricate.Application.Abstractions;
using Fabricate.Application.Llm;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Llm;

/// <summary>
/// One adapter for everything that speaks the OpenAI chat-completions wire format: OpenAI, Azure OpenAI,
/// vLLM, Ollama and gateways such as OpenRouter. Tolerates the usual divergences — absent <c>usage</c> on
/// streamed responses, tool-call arguments arriving in fragments, keyless local endpoints.
/// </summary>
public sealed class OpenAiCompatibleChatCompletionClient : IChatCompletionClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>Azure OpenAI data-plane API version used when the endpoint is a bare resource root.</summary>
    public const string DefaultAzureApiVersion = "2024-10-21";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly bool _isAzureOpenAi;

    public OpenAiCompatibleChatCompletionClient(HttpClient http, string baseUrl, string? apiKey)
    {
        _http = http;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _baseUrl = baseUrl.Trim().TrimEnd('/');

        // Azure OpenAI differs from every other OpenAI-compatible host in two ways: keys go in an `api-key` header
        // (Bearer is reserved for Entra tokens), and the path names a deployment and requires an api-version.
        var host = Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
        _isAzureOpenAi = host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase);
    }

    public string ProviderId => _isAzureOpenAi ? "azure-openai" : "openai-compatible";

    /// <summary>
    /// An endpoint that already names <c>/chat/completions</c> (query string included) is used verbatim. Otherwise the
    /// route is appended — for Azure, the deployment route, with the model id doubling as the deployment name.
    /// </summary>
    private Uri ResolveEndpoint(string model)
    {
        if (_baseUrl.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return new Uri(_baseUrl);

        if (_isAzureOpenAi)
            return new Uri($"{_baseUrl}/openai/deployments/{Uri.EscapeDataString(model)}/chat/completions?api-version={DefaultAzureApiVersion}");

        return new Uri(_baseUrl + "/chat/completions");
    }

    public ModelCapabilities Capabilities { get; } = new(
        SupportsSampling: true,
        SupportsStreaming: true,
        SupportsToolCalling: true,
        SupportsEffort: false,
        SupportsStructuredOutput: true,
        MaxOutputTokens: 32_000);

    public async Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(request, stream: false, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        JsonNode root;
        try
        {
            root = JsonNode.Parse(json) ?? throw new JsonException("empty body");
        }
        catch (JsonException ex)
        {
            throw new LlmProviderException(LlmFailureKind.ProviderError, "The provider returned a malformed response.", ex);
        }

        var choice = (root["choices"] is JsonArray { Count: > 0 } choices ? choices[0] : null)
            ?? throw new LlmProviderException(LlmFailureKind.ProviderError, "The provider returned no choices.");
        var message = choice["message"];
        var text = message?["content"]?.GetValue<string>();
        var calls = (message?["tool_calls"] as JsonArray)?.Select(tc => new LlmToolCall(
            tc?["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
            tc?["function"]?["name"]?.GetValue<string>() ?? string.Empty,
            tc?["function"]?["arguments"]?.GetValue<string>() ?? "{}")).ToArray() ?? [];

        var usage = ReadUsage(root["usage"]);
        var stop = MapFinishReason(choice["finish_reason"]?.GetValue<string>(), calls.Length);

        return new ChatCompletionResult(string.IsNullOrEmpty(text) ? null : text, calls, stop, usage, root["model"]?.GetValue<string>() ?? request.Model);
    }

    public async IAsyncEnumerable<ChatCompletionChunk> StreamAsync(ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(request, stream: true, cancellationToken).ConfigureAwait(false);
        using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(body, Encoding.UTF8);

        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        string? finishReason = null;
        TokenUsage usage = TokenUsage.Zero;
        var modelId = request.Model;

        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new LlmProviderException(LlmFailureKind.Transport, "The provider stream was interrupted.", ex);
            }

            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line["data:".Length..].Trim();
            if (payload == "[DONE]") break;

            JsonNode? node;
            try { node = JsonNode.Parse(payload); }
            catch (JsonException) { continue; }
            if (node is null) continue;

            modelId = node["model"]?.GetValue<string>() ?? modelId;
            if (node["usage"] is { } u) usage = ReadUsage(u);

            // The final usage-only chunk carries an empty choices array.
            var choice = node["choices"] is JsonArray { Count: > 0 } choices ? choices[0] : null;
            if (choice is null) continue;

            finishReason = choice["finish_reason"]?.GetValue<string>() ?? finishReason;
            var delta = choice["delta"];
            if (delta is null) continue;

            if (delta["content"]?.GetValue<string>() is { Length: > 0 } piece)
            {
                text.Append(piece);
                yield return new ChatCompletionChunk(piece);
            }

            if (delta["tool_calls"] is JsonArray toolDeltas)
            {
                foreach (var td in toolDeltas)
                {
                    var index = td?["index"]?.GetValue<int>() ?? 0;
                    if (!calls.TryGetValue(index, out var entry))
                    {
                        entry = (td?["id"]?.GetValue<string>() ?? $"call_{index}", td?["function"]?["name"]?.GetValue<string>() ?? string.Empty, new StringBuilder());
                        calls[index] = entry;
                    }
                    else if (td?["function"]?["name"]?.GetValue<string>() is { Length: > 0 } name && entry.Name.Length == 0)
                    {
                        calls[index] = entry = (entry.Id, name, entry.Args);
                    }

                    if (td?["function"]?["arguments"]?.GetValue<string>() is { } args)
                        entry.Args.Append(args);
                }
            }
        }

        var toolCalls = calls.Values.Select(c => new LlmToolCall(c.Id, c.Name, c.Args.Length == 0 ? "{}" : c.Args.ToString())).ToArray();
        var stop = MapFinishReason(finishReason, toolCalls.Length);

        yield return new ChatCompletionChunk(null, new ChatCompletionResult(
            text.Length == 0 ? null : text.ToString(), toolCalls, stop, usage, modelId));
    }

    // ── Wire format ──────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(ChatCompletionRequest request, bool stream, CancellationToken cancellationToken)
    {
        var body = BuildBody(request, stream);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ResolveEndpoint(request.Model))
        {
            Content = new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json"),
        };
        if (_apiKey is not null)
        {
            if (_isAzureOpenAi)
                httpRequest.Headers.TryAddWithoutValidation("api-key", _apiKey);
            else
                httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }
        if (stream)
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmProviderException(LlmFailureKind.Transport, "Could not reach the provider.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmProviderException(LlmFailureKind.Timeout, "The provider did not respond in time.", ex);
        }

        if (response.IsSuccessStatusCode)
            return response;

        var status = response.StatusCode;
        response.Dispose();
        throw status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new LlmProviderException(LlmFailureKind.Authentication, $"The provider rejected the credential ({(int)status})."),
            HttpStatusCode.TooManyRequests => new LlmProviderException(LlmFailureKind.RateLimited, "The provider rate-limited the request (429)."),
            HttpStatusCode.NotFound => new LlmProviderException(LlmFailureKind.InvalidRequest, "The requested model or endpoint was not found (404)."),
            HttpStatusCode.RequestEntityTooLarge => new LlmProviderException(LlmFailureKind.ContextLengthExceeded, "The request exceeded the provider's size limit."),
            >= HttpStatusCode.InternalServerError => new LlmProviderException(LlmFailureKind.ProviderError, $"The provider reported an error ({(int)status})."),
            _ => new LlmProviderException(LlmFailureKind.InvalidRequest, $"The provider rejected the request ({(int)status})."),
        };
    }

    private static JsonObject BuildBody(ChatCompletionRequest request, bool stream)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.SystemInstructions))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.SystemInstructions });

        foreach (var m in request.Messages)
        {
            switch (m.Role)
            {
                case LlmMessageRole.User:
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text ?? string.Empty });
                    break;
                case LlmMessageRole.Tool:
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolResult!.ToolCallId, ["content"] = m.ToolResult.Content });
                    break;
                default:
                    var assistant = new JsonObject { ["role"] = "assistant", ["content"] = m.Text };
                    if (m.ToolCalls is { Count: > 0 } calls)
                    {
                        assistant["tool_calls"] = new JsonArray(calls.Select(c => (JsonNode)new JsonObject
                        {
                            ["id"] = c.Id,
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgumentsJson },
                        }).ToArray());
                    }
                    messages.Add(assistant);
                    break;
            }
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxOutputTokens,
            ["stream"] = stream,
        };
        if (request.Temperature is { } t) body["temperature"] = t;
        if (stream) body["stream_options"] = new JsonObject { ["include_usage"] = true };

        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(tool => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(string.IsNullOrWhiteSpace(tool.InputSchemaJson) ? "{\"type\":\"object\"}" : tool.InputSchemaJson),
                },
            }).ToArray());
        }

        return body;
    }

    private static TokenUsage ReadUsage(JsonNode? usage)
        => usage is null ? TokenUsage.Zero
            : new TokenUsage(usage["prompt_tokens"]?.GetValue<int>() ?? 0, usage["completion_tokens"]?.GetValue<int>() ?? 0);

    private static LlmStopReason MapFinishReason(string? reason, int toolCallCount) => reason switch
    {
        "tool_calls" or "function_call" => LlmStopReason.ToolUse,
        "length" => LlmStopReason.MaxTokens,
        "content_filter" => LlmStopReason.ContentFiltered,
        _ => toolCallCount > 0 ? LlmStopReason.ToolUse : LlmStopReason.EndTurn,
    };
}
