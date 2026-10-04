namespace Autheris.Application.Events.Services;

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>
/// F-EVT-01: Dispatches signed CloudEvents v1.0 payloads with SSRF defense and HMAC-SHA256 signatures.
/// </summary>
public sealed class CloudEventWebhookDispatcher : ICloudEventWebhookDispatcher
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CloudEventWebhookDispatcher> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public CloudEventWebhookDispatcher(
        HttpClient httpClient,
        ILogger<CloudEventWebhookDispatcher> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<CloudEventDeliveryResult> DispatchAsync(
        CloudEventWebhookSubscription subscription,
        CloudEventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(envelope);

        var deliveryId = Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();

        // SEC-EVT-01: SSRF Validation using central EgressUrlPolicy
        if (!Uri.TryCreate(subscription.TargetUrl, UriKind.Absolute, out var targetUri))
        {
            _logger.LogWarning("Webhook delivery {DeliveryId} aborted: Invalid absolute URI.", deliveryId);
            return new CloudEventDeliveryResult(
                deliveryId,
                subscription.Id,
                envelope.Id,
                false,
                400,
                sw.Elapsed,
                "Target URL rejected by SSRF guardrail: Invalid absolute URI.");
        }

        try
        {
            await Autheris.Application.Security.EgressUrlPolicy.ValidateResolvedAsync(targetUri, isDev: false, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Webhook delivery {DeliveryId} aborted by EgressUrlPolicy: {Error}", deliveryId, ex.Message);
            return new CloudEventDeliveryResult(
                deliveryId,
                subscription.Id,
                envelope.Id,
                false,
                400,
                sw.Elapsed,
                $"Target URL rejected by SSRF guardrail: {ex.Message}");
        }

        try
        {
            var payloadJson = JsonSerializer.Serialize(envelope, JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.TargetUrl);

            // CloudEvents v1.0 binary / structured headers
            request.Headers.TryAddWithoutValidation("ce-specversion", envelope.SpecVersion);
            request.Headers.TryAddWithoutValidation("ce-id", envelope.Id);
            request.Headers.TryAddWithoutValidation("ce-type", envelope.Type);
            request.Headers.TryAddWithoutValidation("ce-source", envelope.Source);
            request.Headers.TryAddWithoutValidation("ce-time", envelope.Time.ToString("O"));

            if (!string.IsNullOrWhiteSpace(envelope.TenantId))
            {
                request.Headers.TryAddWithoutValidation("ce-tenantid", envelope.TenantId);
            }

            // SEC-EVT-02: Cryptographic HMAC-SHA256 Signature
            if (!string.IsNullOrWhiteSpace(subscription.HmacSecret))
            {
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(subscription.HmacSecret));
                var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadJson));
                var signatureHex = Convert.ToHexStringLower(hashBytes);
                request.Headers.TryAddWithoutValidation("X-Autheris-Signature", $"sha256={signatureHex}");
            }

            // Custom headers
            if (subscription.CustomHeaders != null)
            {
                foreach (var (k, v) in subscription.CustomHeaders)
                {
                    request.Headers.TryAddWithoutValidation(k, v);
                }
            }

            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/cloudevents+json");

            var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            sw.Stop();

            var statusCode = (int)response.StatusCode;
            var success = response.IsSuccessStatusCode;

            _logger.LogInformation("Webhook delivery {DeliveryId} to {Url} finished with status {StatusCode} in {ElapsedMs}ms",
                deliveryId, subscription.TargetUrl, statusCode, sw.ElapsedMilliseconds);

            return new CloudEventDeliveryResult(
                deliveryId,
                subscription.Id,
                envelope.Id,
                success,
                statusCode,
                sw.Elapsed,
                success ? null : $"HTTP {statusCode}: {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Webhook delivery {DeliveryId} to {Url} failed with exception", deliveryId, subscription.TargetUrl);
            return new CloudEventDeliveryResult(
                deliveryId,
                subscription.Id,
                envelope.Id,
                false,
                500,
                sw.Elapsed,
                ex.Message);
        }
    }
}
