namespace Autheris.Api.Middleware;

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

/// <summary>
/// SEC C-1 Edge Stripping: Strips any client-supplied <c>x-autheris-*</c> headers from untrusted clients
/// and strips <c>X-Forwarded-*</c> headers when the connection has no remote IP address (WP-ARC-C-04).
/// </summary>
public sealed class EdgeHeaderStrippingMiddleware
{
    private readonly RequestDelegate _next;

    public EdgeHeaderStrippingMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var headersToRemove = context.Request.Headers.Keys
            .Where(k => k.StartsWith("x-autheris-", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var h in headersToRemove)
        {
            context.Request.Headers.Remove(h);
        }

        return _next(context);
    }
}

/// <summary>
/// Drops <c>X-Forwarded-*</c> headers on connections without a remote IP address (Unix domain socket / in-process host)
/// before <c>UseForwardedHeaders</c> runs so an unknown peer cannot spoof host, scheme, or client IP (WP-ARC-C-04).
/// </summary>
public sealed class UntrustedProxyHeaderStrippingMiddleware
{
    private readonly RequestDelegate _next;

    public UntrustedProxyHeaderStrippingMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Connection.RemoteIpAddress == null)
        {
            context.Request.Headers.Remove("X-Forwarded-For");
            context.Request.Headers.Remove("X-Forwarded-Proto");
            context.Request.Headers.Remove("X-Forwarded-Host");
        }

        return _next(context);
    }
}
