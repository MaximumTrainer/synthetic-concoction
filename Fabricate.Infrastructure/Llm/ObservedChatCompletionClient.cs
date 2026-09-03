using System.Diagnostics;
using System.Runtime.CompilerServices;
using Fabricate.Application.Abstractions;
using Fabricate.Application.Llm;
using Fabricate.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Fabricate.Infrastructure.Llm;

/// <summary>
/// Decorates any <see cref="IChatCompletionClient"/> with the two cross-cutting behaviours #60 §8 asks for:
/// one structured log entry per provider attempt (provider, model, latency, token usage, outcome — never prompt or
/// completion bodies), and bounded exponential-backoff retry of failures the adapter marked retryable.
/// Streams are retried only while nothing has been emitted; once a caller has seen a chunk, a failure propagates.
/// </summary>
public sealed class ObservedChatCompletionClient(
    IChatCompletionClient inner,
    ILogger<ObservedChatCompletionClient> logger,
    int maxRetries,
    TimeSpan baseDelay,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IChatCompletionClient
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public string ProviderId => inner.ProviderId;
    public ModelCapabilities Capabilities => inner.Capabilities;

    public async Task<ChatCompletionResult> CompleteAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var result = await inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
                LogSuccess(request, attempt, watch.Elapsed, result);
                return result;
            }
            catch (LlmProviderException ex)
            {
                LogFailure(request, attempt, watch.Elapsed, ex);
                if (!ex.IsRetryable || attempt > maxRetries)
                    throw;
                await _delay(BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async IAsyncEnumerable<ChatCompletionChunk> StreamAsync(ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var watch = Stopwatch.StartNew();
            var emitted = false;
            ChatCompletionResult? final = null;
            LlmProviderException? failure = null;

            var stream = inner.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
            await using (stream.ConfigureAwait(false))
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await stream.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (LlmProviderException ex)
                    {
                        failure = ex;
                        break;
                    }

                    if (!moved) break;

                    var chunk = stream.Current;
                    if (chunk.Final is not null) final = chunk.Final;
                    emitted = true;
                    yield return chunk;
                }
            }

            if (failure is null)
            {
                LogSuccess(request, attempt, watch.Elapsed, final);
                yield break;
            }

            LogFailure(request, attempt, watch.Elapsed, failure);
            if (emitted || !failure.IsRetryable || attempt > maxRetries)
                throw failure;

            await _delay(BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan BackoffFor(int attempt) => TimeSpan.FromTicks(baseDelay.Ticks * (1L << (attempt - 1)));

    private void LogSuccess(ChatCompletionRequest request, int attempt, TimeSpan latency, ChatCompletionResult? result)
        => logger.LogInformation(
            "LLM call {Provider}/{Model} attempt {Attempt} {Outcome} in {LatencyMs} ms: {InputTokens} in / {OutputTokens} out, stop={StopReason}",
            inner.ProviderId, request.Model, attempt, "Success", (long)latency.TotalMilliseconds,
            result?.Usage.InputTokens ?? 0, result?.Usage.OutputTokens ?? 0, result?.StopReason.ToString() ?? "unknown");

    private void LogFailure(ChatCompletionRequest request, int attempt, TimeSpan latency, LlmProviderException ex)
        => logger.LogWarning(
            "LLM call {Provider}/{Model} attempt {Attempt} {Outcome} in {LatencyMs} ms (retryable={Retryable}): {Reason}",
            inner.ProviderId, request.Model, attempt, ex.Kind.ToString(), (long)latency.TotalMilliseconds, ex.IsRetryable, ex.Message);
}
