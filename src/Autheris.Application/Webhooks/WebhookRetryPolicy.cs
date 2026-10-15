namespace Autheris.Application.Webhooks;

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Authentication;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>Outcome class of one delivery attempt.</summary>
public enum WebhookRetryOutcome
{
    Success,
    Retry,
    Permanent
}

/// <summary>Classification of one attempt: outcome, stable error code and the server-requested delay, if any.</summary>
public readonly record struct WebhookRetryDecision(WebhookRetryOutcome Outcome, string? ErrorCode, TimeSpan? RetryAfter);

/// <summary>
/// Classifies delivery results (permanent vs. retryable) and computes full-jitter exponential backoff.
/// Error codes are stable identifiers from <see cref="WebhookDeliveryErrorCodes"/>; exception messages never leave this type.
/// </summary>
public sealed class WebhookRetryPolicy(WebhooksOptions options, TimeProvider timeProvider, Func<double>? jitter = null)
{
    /// <summary>Key in <see cref="Exception.Data"/> marking an egress rejection caused by DNS (value <c>"unresolved"</c>).</summary>
    public const string EgressReasonKey = "EgressReason";

    /// <summary>Value of <see cref="EgressReasonKey"/> for unresolvable hosts.</summary>
    public const string EgressReasonUnresolved = "unresolved";

    private readonly Func<double> _jitter = jitter ?? Random.Shared.NextDouble;

    public WebhookRetryDecision Classify(int statusCode, RetryConditionHeaderValue? retryAfter)
    {
        if (statusCode is >= 200 and < 300)
        {
            return new WebhookRetryDecision(WebhookRetryOutcome.Success, null, null);
        }

        if (statusCode is >= 300 and < 400)
        {
            // Redirects are never followed (SSRF); a redirecting endpoint is misconfigured.
            return new WebhookRetryDecision(WebhookRetryOutcome.Permanent, WebhookDeliveryErrorCodes.HttpRedirect, null);
        }

        if (statusCode is 408 or 429 or >= 500)
        {
            return new WebhookRetryDecision(WebhookRetryOutcome.Retry, WebhookDeliveryErrorCodes.HttpStatus, ParseRetryAfter(retryAfter));
        }

        return new WebhookRetryDecision(WebhookRetryOutcome.Permanent, WebhookDeliveryErrorCodes.HttpStatus, null);
    }

    public WebhookRetryDecision Classify(Exception ex, bool shutdownRequested)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var security = ex as SecurityException ?? ex.InnerException as SecurityException;
        if (security is not null)
        {
            return Equals(security.Data[EgressReasonKey], EgressReasonUnresolved)
                ? new WebhookRetryDecision(WebhookRetryOutcome.Retry, WebhookDeliveryErrorCodes.DnsUnresolved, null)
                : new WebhookRetryDecision(WebhookRetryOutcome.Permanent, WebhookDeliveryErrorCodes.EgressDenied, null);
        }

        if (ex is OperationCanceledException)
        {
            return new WebhookRetryDecision(
                WebhookRetryOutcome.Retry,
                shutdownRequested ? null : WebhookDeliveryErrorCodes.Timeout,
                null);
        }

        if (ex is AuthenticationException || ex.InnerException is AuthenticationException)
        {
            return new WebhookRetryDecision(WebhookRetryOutcome.Retry, WebhookDeliveryErrorCodes.Tls, null);
        }

        if (ex is UriFormatException)
        {
            return new WebhookRetryDecision(WebhookRetryOutcome.Permanent, WebhookDeliveryErrorCodes.InvalidUrl, null);
        }

        return new WebhookRetryDecision(WebhookRetryOutcome.Retry, WebhookDeliveryErrorCodes.Network, null);
    }

    /// <summary>
    /// Full jitter: U(0, min(cap, base * 2^(attempt-1))). A server-requested <paramref name="retryAfter"/> (capped) wins when larger.
    /// </summary>
    public TimeSpan ComputeDelay(int attempt, TimeSpan? retryAfter)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var ceiling = Math.Min(options.EffectiveRetryMaxDelaySeconds, options.EffectiveRetryBaseSeconds * Math.Pow(2, exponent));
        var delay = TimeSpan.FromSeconds(Math.Clamp(_jitter(), 0.0, 1.0) * ceiling);

        if (retryAfter is { } requested)
        {
            var capped = Cap(requested);
            if (capped > delay)
            {
                return capped;
            }
        }

        return delay;
    }

    private TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header is null)
        {
            return null;
        }

        TimeSpan? requested = header.Delta ?? (header.Date is { } date ? date - timeProvider.GetUtcNow() : null);
        return requested is { } r ? Cap(r) : null;
    }

    private TimeSpan Cap(TimeSpan value)
    {
        var max = TimeSpan.FromSeconds(options.EffectiveMaxRetryAfterSeconds);
        return value < TimeSpan.Zero ? TimeSpan.Zero : (value > max ? max : value);
    }
}
