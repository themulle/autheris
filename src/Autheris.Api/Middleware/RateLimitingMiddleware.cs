using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Middleware;

public sealed class PreAuthIpRateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly GatewayOptions _gatewayOptions;
    private readonly PreAuthIpRateLimitOptions _options;
    private readonly IRateLimiterService _rateLimiter;

    public PreAuthIpRateLimitingMiddleware(
        RequestDelegate next,
        IOptions<GatewayOptions> options,
        IRateLimiterService rateLimiter)
    {
        _next = next;
        _gatewayOptions = options.Value;
        _options = options.Value.RateLimiting.PreAuthIpRateLimit;
        _rateLimiter = rateLimiter;
    }

    internal static readonly Prometheus.Counter RateLimitExceededCounter = Prometheus.Metrics.CreateCounter(
        "autheris_ratelimit_rejected_total", "Rate limit rejections count", new Prometheus.CounterConfiguration
        {
            LabelNames = new[] { "type" }
        });

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip health probes or when rate limiting is explicitly disabled (warn_disable_rate_limiting).
        // E-13: /metrics is authenticated and therefore rate limited like every other path (no PBKDF2 amplification);
        // /health never runs Basic authentication (see BasicAuthenticationHandler).
        if (context.Request.Path.StartsWithSegments("/health") ||
            _gatewayOptions.IsRateLimitingDisabled)
        {
            await _next(context);
            return;
        }

        string ip = "127.0.0.1";
        if (_gatewayOptions.ReverseProxy.Enabled && context.Connection.RemoteIpAddress != null)
        {
            ip = context.Connection.RemoteIpAddress.ToString();
        }
        else if (context.Items.TryGetValue("OriginalTcpRemoteIp", out var origIpObj))
        {
            if (origIpObj is System.Net.IPAddress origIp)
            {
                ip = origIp.ToString();
            }
            else if (origIpObj is string origIpStr && !string.IsNullOrWhiteSpace(origIpStr))
            {
                ip = origIpStr;
            }
        }
        else if (context.Connection.RemoteIpAddress != null)
        {
            ip = context.Connection.RemoteIpAddress.ToString();
        }

        // SEC M-08: Aggregate IPv6 clients to /64 so address rotation within a prefix does not yield fresh buckets.
        ip = Autheris.Infrastructure.RateLimiting.ClientIpRateLimitKey.Normalize(ip);

        var result = await _rateLimiter.CheckPreAuthIpAsync(ip, _options, context.RequestAborted);

        if (!result.Allowed)
        {
            RateLimitExceededCounter.WithLabels("pre_auth_ip").Inc();
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                errors = new[]
                {
                    new
                    {
                        message = "Pre-auth IP rate limit exceeded. Wait before retrying.",
                        extensions = new { code = "RATE_LIMIT_EXCEEDED" }
                    }
                }
            }));
            return;
        }

        await _next(context);
    }
}

public sealed class PostAuthSidRateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly GatewayOptions _gatewayOptions;
    private readonly PostAuthSidRateLimitOptions _options;
    private readonly IRateLimiterService _rateLimiter;

    public PostAuthSidRateLimitingMiddleware(
        RequestDelegate next,
        IOptions<GatewayOptions> options,
        IRateLimiterService rateLimiter)
    {
        _next = next;
        _gatewayOptions = options.Value;
        _options = options.Value.RateLimiting.PostAuthSidRateLimit;
        _rateLimiter = rateLimiter;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip health and metrics endpoints or when rate limiting is explicitly disabled (warn_disable_rate_limiting)
        if (context.Request.Path.StartsWithSegments("/health") ||
            context.Request.Path.StartsWithSegments("/metrics") ||
            _gatewayOptions.IsRateLimitingDisabled)
        {
            await _next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            // Anonymous requests proceed to endpoint for authorization / authentication challenge
            await _next(context);
            return;
        }

        var sid = context.User.GetUserSid()?.Value;
        if (string.IsNullOrEmpty(sid))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                errors = new[]
                {
                    new
                    {
                        message = "Zero-Trust Error: Authenticated user lacks a valid SID claim (PrimarySid, objectSid, or NameIdentifier).",
                        extensions = new { code = "UNAUTHORIZED_NO_SID" }
                    }
                }
            }));
            return;
        }

        var result = await _rateLimiter.CheckPostAuthSidAsync(sid, _options, context.RequestAborted);
        if (!result.Allowed)
        {
            PreAuthIpRateLimitingMiddleware.RateLimitExceededCounter.WithLabels("post_auth_sid").Inc();
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString();
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                errors = new[]
                {
                    new
                    {
                        message = "Per-user SID rate limit exceeded.",
                        extensions = new { code = "RATE_LIMIT_EXCEEDED" }
                    }
                }
            }));
            return;
        }

        await _next(context);
    }
}
