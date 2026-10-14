namespace Autheris.Application.Events.Services;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events.Interfaces;
using Autheris.Application.Security;
using Autheris.Application.Webhooks;
using Autheris.Domain.Options;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-EVT-01: Dispatches signed CloudEvents v1.0 payloads with SSRF defense and HMAC-SHA256 signatures.
/// </summary>
public sealed class CloudEventWebhookDispatcher : ICloudEventWebhookDispatcher
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CloudEventWebhookDispatcher> _logger;
    private readonly IOptions<GatewayOptions>? _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public CloudEventWebhookDispatcher(
        HttpClient httpClient,
        ILogger<CloudEventWebhookDispatcher> logger,
        IOptions<GatewayOptions>? options = null)
    {
        _options = options;
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
            // SR-L-14: only the exception type and the redacted destination are logged (messages may carry resolved IPs).
            _logger.LogWarning("Webhook delivery {DeliveryId} to {Url} aborted by EgressUrlPolicy ({ErrorType})", deliveryId, UrlRedactor.Redact(targetUri), ex.GetType().Name);
            return new CloudEventDeliveryResult(
                deliveryId,
                subscription.Id,
                envelope.Id,
                false,
                400,
                sw.Elapsed,
                "Target URL rejected by SSRF guardrail.");
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

            // SEC-EVT-02 / SR-L-13: HMAC-SHA256 over "{timestamp}.{body}" (D1) plus replay-protection headers.
            if (!string.IsNullOrWhiteSpace(subscription.HmacSecret))
            {
                var timestamp = WebhookSignature.FormatTimestamp(DateTimeOffset.UtcNow);
                request.Headers.TryAddWithoutValidation(WebhookSignature.TimestampHeader, timestamp);
                request.Headers.TryAddWithoutValidation("X-Autheris-Event-Id", envelope.Id);
                request.Headers.TryAddWithoutValidation("X-Autheris-Event-Type", envelope.Type);
                request.Headers.TryAddWithoutValidation(
                    WebhookSignature.SignatureHeader,
                    WebhookSignature.SignHeaderValue(subscription.HmacSecret, timestamp, payloadJson));

                // D7: deprecated body-only signature, kept for one release.
                if (_options?.Value.Webhooks.EmitLegacySignature ?? true)
                {
                    request.Headers.TryAddWithoutValidation(
                        WebhookSignature.LegacySignatureHeader,
                        WebhookSignature.Prefix + WebhookSignature.ComputeHex(subscription.HmacSecret, payloadJson));
                }
            }

            // Custom headers
            if (subscription.CustomHeaders != null)
            {
                foreach (var (k, v) in subscription.CustomHeaders)
                {
                    // Review G5: hop-by-hop, routing and signature headers can never be set by a subscription.
                    if (IsForbiddenCustomHeader(k))
                    {
                        _logger.LogWarning("Webhook delivery {DeliveryId}: custom header '{Header}' is not permitted and was dropped.", deliveryId, k);
                        continue;
                    }

                    request.Headers.TryAddWithoutValidation(k, v);
                }
            }

            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/cloudevents+json");

            var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            sw.Stop();

            var statusCode = (int)response.StatusCode;
            var success = response.IsSuccessStatusCode;

            _logger.LogInformation("Webhook delivery {DeliveryId} to {Url} finished with status {StatusCode} in {ElapsedMs}ms",
                deliveryId, UrlRedactor.Redact(targetUri), statusCode, sw.ElapsedMilliseconds);

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
            _logger.LogError("Webhook delivery {DeliveryId} to {Url} failed with {ErrorType}", deliveryId, UrlRedactor.Redact(targetUri), ex.GetType().Name);
            return new CloudEventDeliveryResult(
                deliveryId,
                subscription.Id,
                envelope.Id,
                false,
                500,
                sw.Elapsed,
                "Webhook delivery failed.");
        }
    }

    private static readonly HashSet<string> ForbiddenCustomHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "forwarded", "via", "x-real-ip", "connection", "keep-alive", "te", "trailer", "transfer-encoding",
        "upgrade", "expect", "content-length", "content-type", "content-encoding", "x-autheris-signature", "x-autheris-signature-256", "x-autheris-timestamp",
        "x-autheris-event-id", "x-autheris-event-type"
    };

    /// <summary>
    /// Review G5: true for headers a subscription must not control (Host, Proxy-*, Forwarded, X-Forwarded-*, hop-by-hop,
    /// framing, CloudEvents ce-* and the gateway signature header).
    /// </summary>
    internal static bool IsForbiddenCustomHeader(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        var n = name.Trim();
        return ForbiddenCustomHeaders.Contains(n) ||
               n.StartsWith("proxy-", StringComparison.OrdinalIgnoreCase) ||
               n.StartsWith("x-forwarded-", StringComparison.OrdinalIgnoreCase) ||
               n.StartsWith("ce-", StringComparison.OrdinalIgnoreCase);
    }
}
