namespace Autheris.Application.Webhooks;

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security;
using Autheris.Domain.Events;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
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

    private const int MaxBackoffMilliseconds = 5000;

    private readonly HttpClient _httpClient;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<WebhookDispatcherService> _logger;
    private readonly IHostEnvironment? _environment;

    public WebhookDispatcherService(
        HttpClient httpClient,
        IOptions<GatewayOptions> options,
        ILogger<WebhookDispatcherService> logger,
        IHostEnvironment? environment = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _environment = environment;
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
            Data: EventRedaction.Sanitize(@event, JsonOptions), // SR-L-15 safety net
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
        var timestamp = WebhookSignature.FormatTimestamp(DateTimeOffset.UtcNow);
        var maxRetries = subscription.EffectiveMaxAttempts;

        if (!Uri.TryCreate(subscription.TargetUrl, UriKind.Absolute, out var targetUri))
        {
            _logger.LogWarning("Webhook subscription {SubscriptionId} target URL is malformed. Dropping dispatch.", subscription.Id);
            return;
        }

        var isDev = _environment?.IsDevelopment() ?? false;
        var redactedUrl = UrlRedactor.Redact(targetUri);

        // SR-L-13: fail closed, never deliver unsigned when a secret is required (default: outside Development).
        var hasSecret = !string.IsNullOrEmpty(subscription.Secret);
        if (!hasSecret && (_options.Value.Webhooks.RequireSecret ?? !isDev))
        {
            _logger.LogWarning(
                "Webhook subscription {SubscriptionId} has no secret; delivery to {TargetUrl} dropped (Gateway:Webhooks:RequireSecret).",
                subscription.Id, redactedUrl);
            return;
        }

        var signature = hasSecret ? WebhookSignature.SignHeaderValue(subscription.Secret, timestamp, jsonPayload) : null;
        try
        {
            await EgressUrlPolicy.ValidateResolvedAsync(targetUri, isDev: isDev, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("SSRF violation: Webhook destination {TargetUrl} blocked by egress policy ({ErrorType}).", redactedUrl, ex.GetType().Name);
            return;
        }

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
                if (signature is not null)
                {
                    request.Headers.TryAddWithoutValidation(WebhookSignature.SignatureHeader, signature);
                }

                request.Headers.TryAddWithoutValidation(WebhookSignature.TimestampHeader, timestamp);
                request.Headers.TryAddWithoutValidation("X-Autheris-Event-Type", @event.EventType);
                request.Headers.TryAddWithoutValidation("X-Autheris-Event-Id", @event.EventId);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, defaultTimeoutSeconds)));

                var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug(
                        "Successfully dispatched webhook for {EventType} ({EventId}) to {TargetUrl} [Attempt {Attempt}]",
                        @event.EventType, @event.EventId, redactedUrl, attempt);
                    return;
                }

                _logger.LogWarning(
                    "Webhook endpoint {TargetUrl} responded with HTTP {StatusCode} on attempt {Attempt}/{MaxRetries}",
                    redactedUrl, (int)response.StatusCode, attempt, maxRetries);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("Webhook dispatch cancelled for {TargetUrl}", redactedUrl);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Webhook delivery to {TargetUrl} failed on attempt {Attempt}/{MaxRetries} ({ErrorType})",
                    redactedUrl, attempt, maxRetries, ex.GetType().Name);
            }

            if (attempt < maxRetries)
            {
                // Exponential backoff
                var delayMs = (int)Math.Min(Math.Pow(2, attempt) * 100, MaxBackoffMilliseconds);
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }

        _logger.LogError(
            "Exhausted {MaxRetries} attempts dispatching webhook for {EventType} ({EventId}) to {TargetUrl}",
            maxRetries, @event.EventType, @event.EventId, redactedUrl);
    }

    public static string ComputeHmacSha256(string secret, string payload)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return string.Empty;
        }

        return WebhookSignature.ComputeHex(secret, payload);
    }
}
