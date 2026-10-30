using Autheris.Api.Errors;
using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
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

    private static readonly Meter Meter = new("Autheris.Core");
    internal static readonly Counter<long> RateLimitExceededCounter = Meter.CreateCounter<long>(
        "autheris.ratelimit.rejections", description: "Rate limit rejections count");

    internal static void RecordRejection(string limitKind, string tier = "default") =>
        RateLimitExceededCounter.Add(1,
            new KeyValuePair<string, object?>("limit_kind", limitKind),
            new KeyValuePair<string, object?>("tier", tier));

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

        // Issue #44: Exempt requests carrying an Authorization Bearer token from the pre-auth IP limit.
        // They are strictly governed downstream by PostAuthSidRateLimitingMiddleware (per-identity SID bucket) and CostQuota.
        // WP-PF-08: Basic credentials are exempt in the same way. Failed Basic attempts are throttled by the credential guard BEFORE any password
        // hash is computed (per user+IP and per IP) and unauthenticated requests are charged against this budget after authentication.
        if (_options.ExemptAuthenticatedRequests &&
            context.Request.Headers.TryGetValue("Authorization", out var authHeader) &&
            (authHeader.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
             (_gatewayOptions.Authentication.BasicAuth.Enabled &&
              authHeader.ToString().StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))))
        {
            context.Items["Autheris_PreAuthExempted"] = true;
            await _next(context);
            return;
        }

        string ip = ResolveClientIp(context, _gatewayOptions);

        // SEC M-08: Aggregate IPv6 clients to /64 so address rotation within a prefix does not yield fresh buckets.
        ip = Autheris.Infrastructure.RateLimiting.ClientIpRateLimitKey.Normalize(ip);

        var result = await _rateLimiter.CheckPreAuthIpAsync(ip, _options, context.RequestAborted);

        if (!result.Allowed)
        {
            RecordRejection("pre_auth_ip");
            await ApiProblems.WriteTooManyRequests(context, ApiErrorCodes.RateLimitExceeded, "Pre-auth IP rate limit exceeded. Wait before retrying.", result.RetryAfterSeconds, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await _next(context);
    }

    internal static string ResolveClientIp(HttpContext context, GatewayOptions gatewayOptions)
    {
        string ip = "127.0.0.1";
        if (gatewayOptions.ReverseProxy.Enabled && context.Connection.RemoteIpAddress != null)
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
        return ip;
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
            // Anonymous requests proceed to endpoint for authorization / authentication challenge.
            // Bearer-exempted requests that failed authentication were already IP-limited by
            // UnauthenticatedBearerIpRateLimitingMiddleware (SR-M-02), which runs before authorization.
            await _next(context);
            return;
        }

        var sid = context.User.GetUserSid()?.Value;
        if (string.IsNullOrEmpty(sid))
        {
            await ApiProblems.WriteAsync(context, StatusCodes.Status401Unauthorized, ApiErrorCodes.UnauthorizedNoSid, "Authenticated user lacks a valid SID claim.", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var result = await _rateLimiter.CheckPostAuthSidAsync(sid, _options, context.RequestAborted);
        if (!result.Allowed)
        {
            PreAuthIpRateLimitingMiddleware.RecordRejection("post_auth_sid");
            await ApiProblems.WriteTooManyRequests(context, ApiErrorCodes.RateLimitExceeded, "Per-user SID rate limit exceeded.", result.RetryAfterSeconds, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await _next(context);
    }
}

/// <summary>
/// SR-M-02: Requests that skipped the pre-auth IP limit because they carry a Bearer header (issue #44) but are NOT
/// authenticated after <c>UseAuthentication</c> are charged against the pre-auth IP budget here. This middleware must be
/// placed between authentication and authorization so that 401/403 short-circuits cannot be used for unlimited token guessing.
/// </summary>
public sealed class UnauthenticatedBearerIpRateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly GatewayOptions _gatewayOptions;
    private readonly IRateLimiterService _rateLimiter;

    public UnauthenticatedBearerIpRateLimitingMiddleware(
        RequestDelegate next,
        IOptions<GatewayOptions> options,
        IRateLimiterService rateLimiter)
    {
        _next = next;
        _gatewayOptions = options.Value;
        _rateLimiter = rateLimiter;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (_gatewayOptions.IsRateLimitingDisabled ||
            context.Request.Path.StartsWithSegments("/health") ||
            context.User.Identity?.IsAuthenticated == true ||
            !context.Items.ContainsKey("Autheris_PreAuthExempted"))
        {
            await _next(context);
            return;
        }

        string clientIp = PreAuthIpRateLimitingMiddleware.ResolveClientIp(context, _gatewayOptions);
        clientIp = Autheris.Infrastructure.RateLimiting.ClientIpRateLimitKey.Normalize(clientIp);
        var preAuthOptions = _gatewayOptions.RateLimiting.PreAuthIpRateLimit;
        var ipResult = await _rateLimiter.CheckPreAuthIpAsync(clientIp, preAuthOptions, context.RequestAborted);
        if (!ipResult.Allowed)
        {
            PreAuthIpRateLimitingMiddleware.RecordRejection("pre_auth_ip");
            await ApiProblems.WriteTooManyRequests(context, ApiErrorCodes.RateLimitExceeded, "Pre-auth IP rate limit exceeded. Wait before retrying.", ipResult.RetryAfterSeconds, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await _next(context);
    }
}
