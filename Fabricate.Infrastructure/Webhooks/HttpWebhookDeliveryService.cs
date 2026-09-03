using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fabricate.Application.Abstractions;
using Fabricate.Domain.Models;

namespace Fabricate.Infrastructure.Webhooks;

/// <summary>
/// Delivers webhook events over HTTP with optional HMAC-SHA256 signing, 
/// 10-second timeout, and up to 3 attempts with exponential back-off.
/// </summary>
public sealed class HttpWebhookDeliveryService(
    IWebhookRepository webhookRepository,
    IHttpClientFactory httpClientFactory) : IWebhookDeliveryService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const int MaxAttempts = 3;

    public async Task DeliverAsync(Guid workspaceId, string eventName, object payload, CancellationToken cancellationToken = default)
    {
        var webhooks = await webhookRepository.ListByWorkspaceAsync(workspaceId, cancellationToken).ConfigureAwait(false);
        var active = webhooks.Where(w => w.IsActive && w.Events.Contains(eventName, StringComparer.OrdinalIgnoreCase)).ToList();

        var payloadJson = JsonSerializer.Serialize(payload, JsonOpts);

        var tasks = active.Select(w => DeliverToWebhookAsync(w, eventName, payloadJson, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task DeliverToWebhookAsync(WebhookRegistration webhook, string eventName, string payloadJson, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("webhook");
        var lastError = string.Empty;
        var lastStatus = 0;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, webhook.Url);
                request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                request.Headers.TryAddWithoutValidation("X-Fabricate-Event", eventName);
                request.Headers.TryAddWithoutValidation("X-Fabricate-Delivery", Guid.NewGuid().ToString());

                if (!string.IsNullOrWhiteSpace(webhook.SigningSecret))
                {
                    var signature = ComputeHmacSignature(payloadJson, webhook.SigningSecret);
                    request.Headers.TryAddWithoutValidation("X-Fabricate-Signature", $"sha256={signature}");
                }

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                lastStatus = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    await RecordDeliveryAsync(webhook.Id, eventName, payloadJson, lastStatus, true, null, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                lastError = $"HTTP {lastStatus}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex.Message;
                lastStatus = 0;
            }
        }

        await RecordDeliveryAsync(webhook.Id, eventName, payloadJson, lastStatus, false, lastError, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RecordDeliveryAsync(Guid webhookId, string eventName, string payload, int status, bool succeeded, string? error, CancellationToken cancellationToken)
    {
        var delivery = new WebhookDelivery(
            Id: Guid.NewGuid(),
            WebhookId: webhookId,
            Event: eventName,
            Payload: payload,
            HttpStatus: status,
            Succeeded: succeeded,
            Error: error,
            DeliveredAt: DateTimeOffset.UtcNow);

        await webhookRepository.SaveDeliveryAsync(delivery, cancellationToken).ConfigureAwait(false);
    }

    public static string ComputeHmacSignature(string payload, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(keyBytes, payloadBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
