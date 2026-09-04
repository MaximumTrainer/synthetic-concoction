using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fabricate.Application.Llm;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Llm;
using FluentAssertions;

namespace Fabricate.Tests.Infrastructure;

/// <summary>Wire-format mapping for the OpenAI-compatible adapter against a canned handler; no network.</summary>
public sealed class OpenAiCompatibleChatCompletionClientTests
{
    private static ChatCompletionRequest Request(params LlmToolDefinition[] tools) => new(
        "gpt-x", "Be terse.",
        [LlmMessage.User("hi"), LlmMessage.Assistant("hello", [new LlmToolCall("c1", "echo", """{"a":1}""")]), LlmMessage.FromToolResult(new LlmToolResult("c1", "{\"ok\":true}"))],
        tools, 256, Temperature: 0.2);

    [Fact]
    public async Task Complete_SendsOpenAiShape_AndMapsResponse()
    {
        var handler = new CannedHandler(_ => Json(HttpStatusCode.OK, """
            {"id":"x","model":"gpt-x-2","choices":[{"message":{"role":"assistant","content":"Hi there",
             "tool_calls":[{"id":"call_9","type":"function","function":{"name":"echo","arguments":"{\"v\":2}"}}]},"finish_reason":"tool_calls"}],
             "usage":{"prompt_tokens":12,"completion_tokens":7}}
            """));
        var client = new OpenAiCompatibleChatCompletionClient(new HttpClient(handler), "https://api.example.com/v1", "sk-test");

        var result = await client.CompleteAsync(Request(new LlmToolDefinition("echo", "Echoes", """{"type":"object","properties":{"v":{"type":"number"}}}""")));

        result.Text.Should().Be("Hi there");
        result.ToolCalls.Should().ContainSingle().Which.Should().Be(new LlmToolCall("call_9", "echo", """{"v":2}"""));
        result.StopReason.Should().Be(LlmStopReason.ToolUse);
        result.Usage.Should().Be(new TokenUsage(12, 7));
        result.ModelId.Should().Be("gpt-x-2");

        var sent = handler.LastRequest!;
        sent.RequestUri!.ToString().Should().Be("https://api.example.com/v1/chat/completions");
        sent.Headers.Authorization!.Parameter.Should().Be("sk-test");
        var body = JsonNode.Parse(handler.LastBody!)!;
        body["model"]!.GetValue<string>().Should().Be("gpt-x");
        body["temperature"]!.GetValue<double>().Should().Be(0.2);
        body["messages"]![0]!["role"]!.GetValue<string>().Should().Be("system");
        body["messages"]![2]!["tool_calls"]![0]!["function"]!["name"]!.GetValue<string>().Should().Be("echo");
        body["messages"]![3]!["role"]!.GetValue<string>().Should().Be("tool");
        body["messages"]![3]!["tool_call_id"]!.GetValue<string>().Should().Be("c1");
        body["tools"]![0]!["function"]!["parameters"]!["properties"]!["v"]!["type"]!.GetValue<string>().Should().Be("number");
    }

    [Fact]
    public async Task Complete_KeylessEndpoint_SendsNoAuthorizationHeader()
    {
        var handler = new CannedHandler(_ => Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}"""));
        var client = new OpenAiCompatibleChatCompletionClient(new HttpClient(handler), "http://ollama.local:11434/v1/", null);

        var result = await client.CompleteAsync(Request());

        handler.LastRequest!.Headers.Authorization.Should().BeNull();
        result.StopReason.Should().Be(LlmStopReason.EndTurn);
        result.Usage.Should().Be(TokenUsage.Zero, "usage is optional on some gateways");
    }

    [Fact]
    public async Task AzureOpenAi_UsesTheApiKeyHeader_AndKeepsTheDeploymentUrlAndApiVersion()
    {
        var handler = new CannedHandler(_ => Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}"""));
        var client = new OpenAiCompatibleChatCompletionClient(new HttpClient(handler),
            "https://myres.openai.azure.com/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21", "azure-key");

        await client.CompleteAsync(Request());

        var sent = handler.LastRequest!;
        sent.RequestUri!.ToString().Should().Be("https://myres.openai.azure.com/openai/deployments/gpt-4o/chat/completions?api-version=2024-10-21",
            "an explicit chat-completions URL, including its query string, is used verbatim");
        sent.Headers.Authorization.Should().BeNull("Azure OpenAI rejects Bearer for API keys");
        sent.Headers.TryGetValues("api-key", out var keys).Should().BeTrue();
        keys!.Single().Should().Be("azure-key");
    }

    [Fact]
    public async Task AzureOpenAi_ResourceRootEndpoint_IsCompletedWithTheDeploymentPath()
    {
        var handler = new CannedHandler(_ => Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}"""));
        var client = new OpenAiCompatibleChatCompletionClient(new HttpClient(handler), "https://myres.openai.azure.com", "azure-key");

        await client.CompleteAsync(Request() with { Model = "gpt-4o" });

        handler.LastRequest!.RequestUri!.ToString().Should().Be(
            $"https://myres.openai.azure.com/openai/deployments/gpt-4o/chat/completions?api-version={OpenAiCompatibleChatCompletionClient.DefaultAzureApiVersion}",
            "the model id doubles as the Azure deployment name");
        handler.LastRequest.Headers.TryGetValues("api-key", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GeminiOpenAiCompatibleEndpoint_UsesBearerAuth()
    {
        var handler = new CannedHandler(_ => Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}"""));
        var client = new OpenAiCompatibleChatCompletionClient(new HttpClient(handler), "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-key");

        await client.CompleteAsync(Request() with { Model = "gemini-2.5-pro" });

        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions");
        handler.LastRequest.Headers.Authorization!.Parameter.Should().Be("gemini-key");
        handler.LastRequest.Headers.Contains("api-key").Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, LlmFailureKind.Authentication)]
    [InlineData(HttpStatusCode.TooManyRequests, LlmFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.NotFound, LlmFailureKind.InvalidRequest)]
    [InlineData(HttpStatusCode.BadGateway, LlmFailureKind.ProviderError)]
    public async Task Complete_TranslatesHttpFailures_WithoutLeakingTheKey(HttpStatusCode status, LlmFailureKind expected)
    {
        var handler = new CannedHandler(_ => Json(status, """{"error":{"message":"nope, key sk-test is bad"}}"""));
        var client = new OpenAiCompatibleChatCompletionClient(new HttpClient(handler), "https://api.example.com/v1", "sk-test");

        var act = () => client.CompleteAsync(Request());

        var ex = (await act.Should().ThrowAsync<LlmProviderException>()).Which;
        ex.Kind.Should().Be(expected);
        ex.Message.Should().NotContain("sk-test");
    }

    [Fact]
    public async Task Stream_ParsesSse_AccumulatesToolCallFragments_AndUsage()
    {
        const string sse = """
            data: {"model":"gpt-x-2","choices":[{"delta":{"role":"assistant","content":"Hel"}}]}

            data: {"choices":[{"delta":{"content":"lo"}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"echo","arguments":"{\"v\""}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":":3}"}}]}}]}

            data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}

            data: {"choices":[],"usage":{"prompt_tokens":5,"completion_tokens":9}}

            data: [DONE]

            """;
        var handler = new CannedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var client = new OpenAiCompatibleChatCompletionClient(new HttpClient(handler), "https://api.example.com/v1", "k");

        var chunks = new List<ChatCompletionChunk>();
        await foreach (var c in client.StreamAsync(Request())) chunks.Add(c);

        chunks.Where(c => c.TextDelta is not null).Select(c => c.TextDelta).Should().Equal("Hel", "lo");
        var final = chunks.Last().Final!;
        final.Text.Should().Be("Hello");
        final.ToolCalls.Should().ContainSingle().Which.Should().Be(new LlmToolCall("call_1", "echo", """{"v":3}"""));
        final.StopReason.Should().Be(LlmStopReason.ToolUse);
        final.Usage.Should().Be(new TokenUsage(5, 9));
        final.ModelId.Should().Be("gpt-x-2");

        var body = JsonNode.Parse(handler.LastBody!)!;
        body["stream"]!.GetValue<bool>().Should().BeTrue();
        body["stream_options"]!["include_usage"]!.GetValue<bool>().Should().BeTrue();
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    internal sealed class CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }
}
