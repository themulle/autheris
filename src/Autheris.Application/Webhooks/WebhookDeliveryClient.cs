namespace Autheris.Application.Webhooks;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Security;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>Subscription data resolved at delivery time (never stored in the outbox row).</summary>
public sealed record ResolvedWebhookTarget(
    string TargetUrl,
    string? Secret,
    IReadOnlyDictionary<string, string>? CustomHeaders,
    bool IsActive);

/// <summary>Result of one delivery attempt: classification plus the HTTP status when a response was received.</summary>
public sealed record WebhookDeliveryAttemptResult(WebhookRetryDecision Decision, int? StatusCode);

/// <summary>
/// Single outbound delivery path for configured and CloudEvents webhooks (WP-ARC-A-02). Every attempt re-validates the egress target,
/// takes a fresh timestamp, signs <c>"{timestamp}.{body}"</c> and disposes the response. Only redacted URLs, stable error codes and
/// exception type names are logged; exception messages and resolved addresses never are.
/// </summary>
public sealed class WebhookDeliveryClient
{
    private static readonly HashSet<string> ForbiddenCustomHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "forwarded", "via", "x-real-ip", "connection", "keep-alive", "te", "trailer", "transfer-encoding",
        "upgrade", "expect", "content-length", "content-type", "content-encoding", "x-autheris-signature", "x-autheris-signature-256", "x-autheris-timestamp",
        "x-autheris-event-id", "x-autheris-event-type"
    };

    private readonly HttpClient _httpClient;
    private readonly IOptions<GatewayOptions> _options;
    private readonly WebhookRetryPolicy _retryPolicy;
    private readonly ILogger<WebhookDeliveryClient> _logger;
    private readonly IHostEnvironment? _environment;
    private readonly TimeProvider _timeProvider;
    private readonly Func<Uri, bool, CancellationToken, Task> _egressValidator;

    public WebhookDeliveryClient(
        HttpClient httpClient,
        IOptions<GatewayOptions> options,
        WebhookRetryPolicy retryPolicy,
        ILogger<WebhookDeliveryClient> logger,
        IHostEnvironment? environment = null,
        TimeProvider? timeProvider = null)
        : this(httpClient, options, retryPolicy, logger, environment, timeProvider, null)
    {
    }

    /// <summary>Test seam: the egress validator defaults to <see cref="EgressUrlPolicy.ValidateResolvedAsync"/>.</summary>
    internal WebhookDeliveryClient(
        HttpClient httpClient,
        IOptions<GatewayOptions> options,
        WebhookRetryPolicy retryPolicy,
        ILogger<WebhookDeliveryClient> logger,
        IHostEnvironment? environment,
        TimeProvider? timeProvider,
        Func<Uri, bool, CancellationToken, Task>? egressValidator)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _retryPolicy = retryPolicy ?? throw new ArgumentNullException(nameof(retryPolicy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _environment = environment;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _egressValidator = egressValidator ?? ((uri, isDev, ct) => EgressUrlPolicy.ValidateResolvedAsync(uri, isDev, ct));
    }

    public async Task<WebhookDeliveryAttemptResult> DeliverAsync(WebhookOutboxMessage message, ResolvedWebhookTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(target);

        var webhooks = _options.Value.Webhooks;
        var isDev = _environment?.IsDevelopment() ?? false;
        var redacted = UrlRedactor.Redact(target.TargetUrl);

        // SR-L-13: fail closed, never deliver unsigned when a secret is required (default: outside Development).
        var hasSecret = !string.IsNullOrEmpty(target.Secret);
        if (!hasSecret && (webhooks.RequireSecret ?? !isDev))
        {
            _logger.LogWarning(
                "Webhook subscription {SubscriptionId} has no secret; delivery to {TargetUrl} dropped (Gateway:Webhooks:RequireSecret).",
                message.SubscriptionId, redacted);
            return Permanent(WebhookDeliveryErrorCodes.SecretMissing);
        }

        if (!Uri.TryCreate(target.TargetUrl, UriKind.Absolute, out var uri))
        {
            _logger.LogWarning("Webhook subscription {SubscriptionId} target URL is malformed; delivery dropped.", message.SubscriptionId);
            return Permanent(WebhookDeliveryErrorCodes.InvalidUrl);
        }

        try
        {
            // Re-validated on every attempt: DNS may have changed since the previous one (the connect callback pins the IP as well).
            await _egressValidator(uri, isDev, ct).ConfigureAwait(false);

            using var request = BuildRequest(message, target, uri, hasSecret);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, webhooks.DefaultTimeoutSeconds)));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var decision = _retryPolicy.Classify(status, response.Headers.RetryAfter);
            if (decision.Outcome == WebhookRetryOutcome.Success)
            {
                _logger.LogDebug(
                    "Webhook {EventType} ({EventId}) delivered to {TargetUrl} with status {StatusCode}",
                    message.EventType, message.EventId, redacted, status);
            }
            else
            {
                _logger.LogWarning(
                    "Webhook {EventId} to {TargetUrl} answered HTTP {StatusCode} ({ErrorCode})",
                    message.EventId, redacted, status, decision.ErrorCode);
            }

            return new WebhookDeliveryAttemptResult(decision, status);
        }
        catch (Exception ex)
        {
            var decision = _retryPolicy.Classify(ex, ct.IsCancellationRequested);
            _logger.LogWarning(
                "Webhook {EventId} to {TargetUrl} failed ({ErrorCode}, {ErrorType})",
                message.EventId, redacted, decision.ErrorCode ?? "cancelled", ex.GetType().Name);
            return new WebhookDeliveryAttemptResult(decision, null);
        }
    }

    private HttpRequestMessage BuildRequest(WebhookOutboxMessage message, ResolvedWebhookTarget target, Uri uri, bool hasSecret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(message.PayloadJson, Encoding.UTF8, "application/cloudevents+json")
        };

        // CloudEvents v1.0 headers
        request.Headers.TryAddWithoutValidation("ce-specversion", "1.0");
        request.Headers.TryAddWithoutValidation("ce-id", message.EventId);
        request.Headers.TryAddWithoutValidation("ce-type", message.EventType);
        request.Headers.TryAddWithoutValidation("ce-source", message.EventSource);
        request.Headers.TryAddWithoutValidation("ce-time", message.EventTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(message.EventTenantId))
        {
            request.Headers.TryAddWithoutValidation("ce-tenantid", message.EventTenantId);
        }

        // SR-L-13: HMAC-SHA256 over "{timestamp}.{body}" with a fresh timestamp per attempt (replay protection).
        var timestamp = WebhookSignature.FormatTimestamp(_timeProvider.GetUtcNow());
        request.Headers.TryAddWithoutValidation(WebhookSignature.TimestampHeader, timestamp);
        request.Headers.TryAddWithoutValidation("X-Autheris-Event-Id", message.EventId);
        request.Headers.TryAddWithoutValidation("X-Autheris-Event-Type", message.EventType);
        if (hasSecret)
        {
            request.Headers.TryAddWithoutValidation(
                WebhookSignature.SignatureHeader,
                WebhookSignature.SignHeaderValue(target.Secret!, timestamp, message.PayloadJson));
        }

        if (target.CustomHeaders is { Count: > 0 })
        {
            foreach (var (name, value) in target.CustomHeaders)
            {
                // Review G5: hop-by-hop, routing, CloudEvents and signature headers can never be set by a subscription.
                if (IsForbiddenCustomHeader(name))
                {
                    _logger.LogWarning("Webhook subscription {SubscriptionId}: custom header '{Header}' is not permitted and was dropped.", message.SubscriptionId, name);
                    continue;
                }

                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return request;
    }

    /// <summary>
    /// Review G5: true for headers a subscription must not control (Host, Proxy-*, Forwarded, X-Forwarded-*, hop-by-hop,
    /// framing, CloudEvents ce-* and the gateway signature headers, including the removed legacy name so no lookalike can be injected).
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

    private static WebhookDeliveryAttemptResult Permanent(string code) =>
        new(new WebhookRetryDecision(WebhookRetryOutcome.Permanent, code, null), null);
}
