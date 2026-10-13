namespace Autheris.Application.Webhooks;

using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Events;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Dispatches domain events as signed CloudEvents v1.0 webhooks with HMAC-SHA256 signature
/// and exponential backoff retry.
/// </summary>
public sealed class WebhookDispatcherService : IWebhookDispatcherService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly HttpClient _httpClient;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<WebhookDispatcherService> _logger;

    public WebhookDispatcherService(
        HttpClient httpClient,
        IOptions<GatewayOptions> options,
        ILogger<WebhookDispatcherService> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task DispatchEventAsync(IGatewayEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var webhooksConfig = _options.Value.Webhooks;
        if (!webhooksConfig.Enabled || webhooksConfig.Subscriptions.Count == 0)
        {
            return;
        }

        var matchingSubscriptions = webhooksConfig.Subscriptions
            .FindAll(s => s.MatchesEvent(@event.EventType, @event.TenantId));

        if (matchingSubscriptions.Count == 0)
        {
            return;
        }

        var cloudEvent = new GatewayCloudEvent(
            Id: @event.EventId,
            Source: "https://autheris.gateway/events",
            Type: @event.EventType,
            Time: @event.Timestamp,
            Subject: @event.CorrelationId,
            DataContentType: "application/json",
            Data: @event,
            TenantId: @event.TenantId,
            CorrelationId: @event.CorrelationId);

        var jsonPayload = JsonSerializer.Serialize(cloudEvent, JsonOptions);

        var dispatchTasks = matchingSubscriptions.ConvertAll(sub =>
            DeliverWebhookWithRetryAsync(sub, @event, jsonPayload, webhooksConfig.DefaultTimeoutSeconds, ct));

        await Task.WhenAll(dispatchTasks).ConfigureAwait(false);
    }

    private async Task DeliverWebhookWithRetryAsync(
        WebhookSubscription subscription,
        IGatewayEvent @event,
        string jsonPayload,
        int defaultTimeoutSeconds,
        CancellationToken ct)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        var signature = ComputeHmacSha256(subscription.Secret, $"{timestamp}.{jsonPayload}");
        var maxRetries = Math.Max(1, subscription.MaxRetries);

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, subscription.TargetUrl);
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/cloudevents+json");

                // CloudEvents v1.0 standard headers
                request.Headers.TryAddWithoutValidation("ce-specversion", "1.0");
                request.Headers.TryAddWithoutValidation("ce-id", @event.EventId);
                request.Headers.TryAddWithoutValidation("ce-source", "https://autheris.gateway/events");
                request.Headers.TryAddWithoutValidation("ce-type", @event.EventType);
                request.Headers.TryAddWithoutValidation("ce-time", @event.Timestamp.ToString("O"));

                // Autheris HMAC signature & replay protection headers
                request.Headers.TryAddWithoutValidation("X-Autheris-Signature-256", $"sha256={signature}");
                request.Headers.TryAddWithoutValidation("X-Autheris-Timestamp", timestamp);
                request.Headers.TryAddWithoutValidation("X-Autheris-Event-Type", @event.EventType);
                request.Headers.TryAddWithoutValidation("X-Autheris-Event-Id", @event.EventId);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, defaultTimeoutSeconds)));

                var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug(
                        "Successfully dispatched webhook for {EventType} ({EventId}) to {TargetUrl} [Attempt {Attempt}]",
                        @event.EventType, @event.EventId, subscription.TargetUrl, attempt);
                    return;
                }

                _logger.LogWarning(
                    "Webhook endpoint {TargetUrl} responded with HTTP {StatusCode} on attempt {Attempt}/{MaxRetries}",
                    subscription.TargetUrl, (int)response.StatusCode, attempt, maxRetries);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("Webhook dispatch cancelled for {TargetUrl}", subscription.TargetUrl);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Webhook delivery to {TargetUrl} failed on attempt {Attempt}/{MaxRetries}",
                    subscription.TargetUrl, attempt, maxRetries);
            }

            if (attempt < maxRetries)
            {
                // Exponential backoff
                var delayMs = (int)(Math.Pow(2, attempt) * 100);
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }

        _logger.LogError(
            "Exhausted {MaxRetries} attempts dispatching webhook for {EventType} ({EventId}) to {TargetUrl}",
            maxRetries, @event.EventType, @event.EventId, subscription.TargetUrl);
    }

    public static string ComputeHmacSha256(string secret, string payload)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return string.Empty;
        }

        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(payloadBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
