namespace Autheris.Api.Middleware;

using System;
using System.Collections.Frozen;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

/// <summary>
/// SEC C-1 Edge Stripping: Strips untrusted client-supplied internal <c>x-autheris-*</c> trust, routing,
/// federation, and response headers (while allowing inbound client/webhook headers such as
/// <c>X-Autheris-Timestamp</c>, <c>X-Autheris-Delivery</c>, <c>X-Autheris-Signature-256</c>, and <c>X-Autheris-App</c>),
/// and strips <c>X-Forwarded-*</c> headers when the connection has no remote IP address. In <c>Direct</c> mode it also
/// removes every configured ForwardAuth header (identity and secret) so no identity header can reach a direct credential.
/// </summary>
public sealed class EdgeHeaderStrippingMiddleware
{
    private static readonly FrozenSet<string> StrippedInternalHeaders = FrozenSet.ToFrozenSet(
    [
        "X-Autheris-Transport",
        "X-Autheris-Domain",
        "X-Autheris-Principal",
        "X-Autheris-Tenant",
        "X-Autheris-Decision",
        "X-Autheris-Denial-Reason",
        "X-Autheris-Rls-Filter",
        "X-Autheris-Roles",
        "X-Autheris-Groups",
        "X-Autheris-Sid",
        "X-Autheris-User",
        "X-Autheris-Signature",
        "X-Autheris-Nonce",
        "X-Autheris-Body-Sha256",
        "X-Autheris-Signature-Version",
        "X-Autheris-Event-Id",
        "X-Autheris-Event-Type",
        "X-Autheris-Mode",
        "X-Autheris-Cache",
        "X-Autheris-Truncated"
    ], StringComparer.OrdinalIgnoreCase);

    private readonly RequestDelegate _next;
    private readonly string[] _directModeHeaders;

    public EdgeHeaderStrippingMiddleware(RequestDelegate next, IOptions<GatewayOptions> options)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        var auth = options?.Value.Authentication ?? new AuthenticationOptions();
        _directModeHeaders = auth.EffectiveMode == AuthenticationMode.Direct
            ? [.. auth.ForwardAuth.IdentityHeaderNames.Append(auth.ForwardAuth.SharedSecretHeader).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase)]
            : [];
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var h in StrippedInternalHeaders)
        {
            context.Request.Headers.Remove(h);
        }

        foreach (var h in _directModeHeaders)
        {
            if (context.Request.Headers.Remove(h))
            {
                AuthMetrics.IdentityHeadersStripped.Add(1);
            }
        }

        return _next(context);
    }
}

/// <summary>
/// Drops <c>X-Forwarded-*</c> headers on connections without a remote IP address (Unix domain socket / in-process host)
/// before <c>UseForwardedHeaders</c>runs so an unknown peer cannot spoof host, scheme, or client IP.
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
