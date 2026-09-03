using Fabricate.Application.Abstractions;
using Fabricate.Application.Llm;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Llm;

/// <summary>
/// Proves a credential works with the smallest completion the provider will accept, under a hard timeout,
/// so misconfiguration surfaces at setup time rather than mid-conversation.
/// </summary>
public sealed class ChatCompletionCredentialProbe(IChatCompletionClientFactory factory) : ILlmCredentialProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    public async Task<LlmCredentialValidationResult> ProbeAsync(Guid credentialId, ResolvedLlmCredential credential, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProbeTimeout);

        var client = factory.Create(credential);
        var request = new ChatCompletionRequest(
            credential.Model,
            "Reply with the single word OK.",
            [LlmMessage.User("ping")],
            [],
            MaxOutputTokens: 16);

        try
        {
            var result = await client.CompleteAsync(request, cts.Token).ConfigureAwait(false);
            return new LlmCredentialValidationResult(credentialId, true,
                $"Provider responded ({result.Usage.TotalTokens} tokens, model {result.ModelId}).", DateTimeOffset.UtcNow);
        }
        catch (LlmProviderException ex)
        {
            return new LlmCredentialValidationResult(credentialId, false, ex.Message, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LlmCredentialValidationResult(credentialId, false, "The provider did not respond within the probe timeout.", DateTimeOffset.UtcNow);
        }
    }
}
