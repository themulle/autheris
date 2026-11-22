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
/// Edge Header Stripping: Strips only explicitly listed internal/spoofable <c>X-Autheris-*</c> headers from untrusted
/// inbound clients, rather than stripping every <c>x-autheris-*</c> prefix blindly. An explicit internal deny-list
/// prevents spoofing of gateway identity, routing, policy, signature, and diagnostic headers while allowing signed
/// webhook headers (<c>X-Autheris-Timestamp</c>, <c>X-Autheris-Delivery</c>, <c>X-Autheris-Signature-256</c>),
/// app-scoped trusted document headers (<c>X-Autheris-App</c>), and future client headers to pass through unchanged.
/// In <c>Direct</c> mode it also removes every configured ForwardAuth header (identity and secret) so no identity
/// header can reach a direct credential.
/// </summary>
public sealed class EdgeHeaderStrippingMiddleware
{
    private static readonly FrozenSet<string> InternalHeadersToStrip = FrozenSet.ToFrozenSet(
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
        "X-Autheris-Signature",
        "X-Autheris-Nonce",
        "X-Autheris-Signature-Version",
        "X-Autheris-Body-Sha256",
        "X-Autheris-Event-Id",
        "X-Autheris-Event-Type",
        "X-Autheris-Mode",
        "X-Autheris-Truncated",
        "X-Autheris-Cache",
        "X-Autheris-Debug",
        "X-Autheris-Internal",
        "X-Autheris-User",
        "X-Autheris-Scopes",
        "X-Autheris-Subject",
        "X-Autheris-Realm"
    ], StringComparer.OrdinalIgnoreCase);

    private readonly RequestDelegate _next;
    private readonly string[] _directModeHeaders;

    public EdgeHeaderStrippingMiddleware(RequestDelegate next, IOptions<GatewayOptions> options)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        var auth = options?.Value?.Authentication ?? new AuthenticationOptions();
        _directModeHeaders = auth.EffectiveMode == AuthenticationMode.Direct
            ? [.. auth.ForwardAuth.IdentityHeaderNames.Append(auth.ForwardAuth.SharedSecretHeader).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase)]
            : [];
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var header in InternalHeadersToStrip)
        {
            context.Request.Headers.Remove(header);
        }

        foreach (var header in _directModeHeaders)
        {
            if (context.Request.Headers.Remove(header))
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
