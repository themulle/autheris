namespace Autheris.Api.Middleware;

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

/// <summary>
/// SEC C-1 Edge Stripping: Strips any client-supplied <c>x-autheris-*</c> headers from untrusted clients
/// and strips <c>X-Forwarded-*</c>headers when the connection has no remote IP address. In <c>Direct</c> mode it also removes every
/// configured ForwardAuth header (identity and secret) so no identity header can reach a direct credential.
/// </summary>
public sealed class EdgeHeaderStrippingMiddleware
{
    private static readonly System.Diagnostics.Metrics.Counter<long> StrippedTotal =
        new System.Diagnostics.Metrics.Meter("Autheris.Core").CreateCounter<long>(
            "autheris_auth_identity_headers_stripped_total", description: "ForwardAuth identity headers removed from Direct-mode requests.");

    private readonly RequestDelegate _next;
    private readonly string[] _directModeHeaders;

    public EdgeHeaderStrippingMiddleware(RequestDelegate next, Microsoft.Extensions.Options.IOptions<Autheris.Domain.Options.GatewayOptions> options)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        var auth = options?.Value.Authentication ?? new Autheris.Domain.Options.AuthenticationOptions();
        _directModeHeaders = auth.EffectiveMode == Autheris.Domain.Options.AuthenticationMode.Direct
            ? [.. auth.ForwardAuth.GetIdentityHeaderNames().Append(auth.ForwardAuth.SharedSecretHeader).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase)]
            : [];
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

        foreach (var h in _directModeHeaders)
        {
            if (context.Request.Headers.Remove(h))
            {
                StrippedTotal.Add(1);
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
