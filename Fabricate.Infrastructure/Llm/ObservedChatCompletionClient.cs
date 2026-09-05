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
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    ILlmUsageRecorder? usageRecorder = null,
    LlmCallContext? callContext = null,
    Guid? credentialId = null) : IChatCompletionClient
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
                await RecordAsync(request, attempt, watch.Elapsed, result, willRetry: false).ConfigureAwait(false);
                return result;
            }
            catch (LlmProviderException ex)
            {
                LogFailure(request, attempt, watch.Elapsed, ex);
                var willRetry = ex.IsRetryable && attempt <= maxRetries;
                await RecordAsync(request, attempt, watch.Elapsed, result: null, willRetry).ConfigureAwait(false);
                if (!willRetry)
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
                await RecordAsync(request, attempt, watch.Elapsed, final, willRetry: false).ConfigureAwait(false);
                yield break;
            }

            LogFailure(request, attempt, watch.Elapsed, failure);
            var willRetry = !emitted && failure.IsRetryable && attempt <= maxRetries;
            await RecordAsync(request, attempt, watch.Elapsed, result: null, willRetry).ConfigureAwait(false);
            if (!willRetry)
                throw failure;

            await _delay(BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan BackoffFor(int attempt) => TimeSpan.FromTicks(baseDelay.Ticks * (1L << (attempt - 1)));

    /// <summary>
    /// Writes one usage record per attempt (#77). This is the only layer that sees the attempts individually —
    /// the caller sees one call — so a workspace whose calls keep failing and retrying is visible here and
    /// nowhere else.
    /// </summary>
    private async Task RecordAsync(
        ChatCompletionRequest request,
        int attempt,
        TimeSpan latency,
        ChatCompletionResult? result,
        bool willRetry)
    {
        if (usageRecorder is null || callContext is null) return;

        try
        {
            await usageRecorder.RecordAsync(new LlmUsageRecord(
                Guid.NewGuid(),
                callContext.WorkspaceId,
                callContext.ProjectId,
                callContext.SessionId,
                credentialId,
                inner.ProviderId,
                request.Model,
                result?.Usage.InputTokens ?? 0,
                result?.Usage.OutputTokens ?? 0,
                attempt,
                (long)latency.TotalMilliseconds,
                result is not null ? LlmCallOutcome.Success
                    : willRetry ? LlmCallOutcome.RetriedFailure
                    : LlmCallOutcome.Failure,
                DateTimeOffset.UtcNow)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Bookkeeping must never cost the user their answer.
            logger.LogError(ex, "Failed to record LLM usage for {Provider}/{Model}.", inner.ProviderId, request.Model);
        }
    }

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
