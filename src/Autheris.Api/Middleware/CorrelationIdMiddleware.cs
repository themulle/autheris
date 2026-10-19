namespace Autheris.Api.Middleware;

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Captures the physical TCP remote IP prior to <c>UseForwardedHeaders</c> and propagates a canonical correlation ID (WP-ARC-C-04).
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "Autheris.CorrelationId";
    public const string OriginalTcpRemoteIpItemKey = "OriginalTcpRemoteIp";
    private const int MaxCorrelationIdLength = 128;

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Connection.RemoteIpAddress != null)
        {
            context.Items[OriginalTcpRemoteIpItemKey] = context.Connection.RemoteIpAddress;
        }

        var correlationId = ResolveCorrelationId(context);
        context.Items[ItemKey] = correlationId;

        if (Activity.Current != null && Activity.Current.IsAllDataRequested)
        {
            Activity.Current.SetTag(Autheris.Domain.Diagnostics.TelemetryAttributes.AutherisCorrelationId, correlationId);
        }

        if (context.Request.Headers.ContainsKey(HeaderName))
        {
            context.Response.Headers[HeaderName] = correlationId;
        }

        return _next(context);
    }

    public static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            var raw = values.ToString().Trim();
            if (IsSafeCorrelationId(raw))
            {
                return raw;
            }
        }

        return Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
    }

    private static bool IsSafeCorrelationId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxCorrelationIdLength)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (ch < 0x21 || ch > 0x7E)
            {
                return false;
            }
        }

        return true;
    }
}
