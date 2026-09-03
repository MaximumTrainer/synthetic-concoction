using System.Runtime.CompilerServices;
using Fabricate.Application.Abstractions;
using Fabricate.Application.Llm;
using Fabricate.Domain.Models;
using Fabricate.Infrastructure.Llm;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace Fabricate.Tests.Infrastructure;

/// <summary>#60 §8: one structured log line per provider call, and bounded retry of retryable failures only.</summary>
public sealed class ObservedChatCompletionClientTests
{
    private const string Prompt = "SECRET-PROMPT-CONTENT do not log me";
    private readonly FlakyClient _inner = new();
    private readonly CapturingLogger _logger = new();
    private readonly List<TimeSpan> _delays = [];

    private ObservedChatCompletionClient Build(int maxRetries = 2) => new(
        _inner, _logger, maxRetries, TimeSpan.FromMilliseconds(500),
        (delay, _) => { _delays.Add(delay); return Task.CompletedTask; });

    private static ChatCompletionRequest Request() => new("claude-opus-5", "system", [LlmMessage.User(Prompt)], [], 64);

    [Fact]
    public async Task Complete_RetriesRetryableFailures_WithExponentialBackoff_ThenSucceeds()
    {
        _inner.Script(Fail(LlmFailureKind.RateLimited), Fail(LlmFailureKind.Transport), Ok("hi"));

        var result = await Build().CompleteAsync(Request());

        result.Text.Should().Be("hi");
        _inner.Attempts.Should().Be(3);
        _delays.Should().Equal(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000));
    }

    [Theory]
    [InlineData(LlmFailureKind.Authentication)]
    [InlineData(LlmFailureKind.InvalidRequest)]
    [InlineData(LlmFailureKind.ContextLengthExceeded)]
    public async Task Complete_DoesNotRetryNonRetryableFailures(LlmFailureKind kind)
    {
        _inner.Script(Fail(kind), Ok("never"));

        var act = () => Build().CompleteAsync(Request());

        (await act.Should().ThrowAsync<LlmProviderException>()).Which.Kind.Should().Be(kind);
        _inner.Attempts.Should().Be(1);
        _delays.Should().BeEmpty();
    }

    [Fact]
    public async Task Complete_GivesUpAfterMaxRetries_AndRethrowsTheLastFailure()
    {
        _inner.Script(Fail(LlmFailureKind.RateLimited), Fail(LlmFailureKind.ProviderError), Fail(LlmFailureKind.Timeout), Ok("never"));

        var act = () => Build(maxRetries: 2).CompleteAsync(Request());

        (await act.Should().ThrowAsync<LlmProviderException>()).Which.Kind.Should().Be(LlmFailureKind.Timeout);
        _inner.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task EveryAttempt_IsLoggedWithProviderModelLatencyUsageAndOutcome_AndNeverThePrompt()
    {
        _inner.Script(Fail(LlmFailureKind.RateLimited), Ok("hi"));

        await Build().CompleteAsync(Request());

        _logger.Entries.Should().HaveCount(2);
        var failed = _logger.Entries[0];
        failed.Level.Should().Be(LogLevel.Warning);
        failed.State.Should().Contain(kv => kv.Key == "Provider" && (string)kv.Value! == "flaky");
        failed.State.Should().Contain(kv => kv.Key == "Model" && (string)kv.Value! == "claude-opus-5");
        failed.State.Should().Contain(kv => kv.Key == "Outcome" && (string)kv.Value! == "RateLimited");
        failed.State.Should().Contain(kv => kv.Key == "Attempt" && (int)kv.Value! == 1);
        failed.State.Should().Contain(kv => kv.Key == "LatencyMs");

        var ok = _logger.Entries[1];
        ok.Level.Should().Be(LogLevel.Information);
        ok.State.Should().Contain(kv => kv.Key == "Outcome" && (string)kv.Value! == "Success");
        ok.State.Should().Contain(kv => kv.Key == "InputTokens" && (int)kv.Value! == 10);
        ok.State.Should().Contain(kv => kv.Key == "OutputTokens" && (int)kv.Value! == 5);
        ok.State.Should().Contain(kv => kv.Key == "StopReason" && (string)kv.Value! == "EndTurn");

        _logger.Entries.SelectMany(e => e.State).Select(kv => kv.Value?.ToString() ?? "").Should().NotContain(s => s.Contains("SECRET-PROMPT"));
        _logger.Entries.Select(e => e.Message).Should().NotContain(m => m.Contains("SECRET-PROMPT"));
    }

    [Fact]
    public async Task Stream_RetriesOnlyWhenNothingHasBeenEmittedYet()
    {
        _inner.Script(Fail(LlmFailureKind.Transport), Ok("streamed text"));
        var chunks = new List<ChatCompletionChunk>();
        await foreach (var c in Build().StreamAsync(Request())) chunks.Add(c);
        chunks.Last().Final!.Text.Should().Be("streamed text");
        _inner.Attempts.Should().Be(2);

        _inner.Reset();
        _inner.Script(FailMidStream(), Ok("never"));
        var act = async () => { await foreach (var _ in Build().StreamAsync(Request())) { } };
        await act.Should().ThrowAsync<LlmProviderException>("a partial stream was already delivered to the caller");
        _inner.Attempts.Should().Be(1);
    }

    [Fact]
    public void Capabilities_AndProviderId_PassThrough()
    {
        var wrapped = Build();
        wrapped.ProviderId.Should().Be("flaky");
        wrapped.Capabilities.Should().Be(_inner.Capabilities);
    }

    // ── Doubles ───────────────────────────────────────────────────────────────────

    private static Func<Task<ChatCompletionResult>> Ok(string text)
        => () => Task.FromResult(new ChatCompletionResult(text, [], LlmStopReason.EndTurn, new TokenUsage(10, 5), "claude-opus-5"));

    private static Func<Task<ChatCompletionResult>> Fail(LlmFailureKind kind)
        => () => throw new LlmProviderException(kind, $"simulated {kind}");

    private static Func<Task<ChatCompletionResult>> FailMidStream()
        => () => throw new MidStreamFailure();

    private sealed class MidStreamFailure : Exception;

    private sealed class FlakyClient : IChatCompletionClient
    {
        private readonly Queue<Func<Task<ChatCompletionResult>>> _script = new();
        public int Attempts { get; private set; }
        public string ProviderId => "flaky";
        public ModelCapabilities Capabilities { get; } = new(false, true, true, true, true, 4096);
        public void Script(params Func<Task<ChatCompletionResult>>[] steps) { foreach (var s in steps) _script.Enqueue(s); }
        public void Reset() { _script.Clear(); Attempts = 0; }

        public Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken ct = default)
        {
            Attempts++;
            return _script.Dequeue()();
        }

        public async IAsyncEnumerable<ChatCompletionChunk> StreamAsync(ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Attempts++;
            var step = _script.Dequeue();
            ChatCompletionResult? result = null;
            var brokeMidStream = false;
            try
            {
                result = await step();
            }
            catch (MidStreamFailure)
            {
                brokeMidStream = true;
            }

            if (brokeMidStream)
            {
                // A chunk reaches the caller, then the stream dies: the decorator must not restart it.
                yield return new ChatCompletionChunk("partial ");
                throw new LlmProviderException(LlmFailureKind.Transport, "stream broke half way");
            }

            yield return new ChatCompletionChunk(result!.Text);
            yield return new ChatCompletionChunk(null, result);
        }
    }

    internal sealed class CapturingLogger : ILogger<ObservedChatCompletionClient>
    {
        public List<(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var pairs = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            Entries.Add((logLevel, formatter(state, exception), pairs));
        }
    }
}
