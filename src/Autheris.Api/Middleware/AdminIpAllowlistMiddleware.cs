namespace Autheris.Api.Middleware;

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Enforces an optional IP allowlist on administrative endpoints when configured (WP-ARC-C-04).
/// </summary>
public sealed class AdminIpAllowlistMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HashSet<IPAddress> _allowedIps;
    private readonly PathString _adminPrefix;

    public AdminIpAllowlistMiddleware(RequestDelegate next)
        : this(next, Array.Empty<IPAddress>(), new PathString("/api/admin"))
    {
    }

    public AdminIpAllowlistMiddleware(RequestDelegate next, IEnumerable<IPAddress> allowedIps, PathString adminPrefix = default)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        ArgumentNullException.ThrowIfNull(allowedIps);
        _allowedIps = new HashSet<IPAddress>(allowedIps);
        _adminPrefix = adminPrefix.HasValue ? adminPrefix : new PathString("/api/admin");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_allowedIps.Count > 0 &&
            context.Request.Path.StartsWithSegments(_adminPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var remoteIp = context.Connection.RemoteIpAddress;
            if (remoteIp is { IsIPv4MappedToIPv6: true })
            {
                remoteIp = remoteIp.MapToIPv4();
            }

            if (remoteIp == null || !_allowedIps.Contains(remoteIp))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                var problem = ApiProblems.CreateProblemDetails(
                    StatusCodes.Status403Forbidden,
                    ApiErrorCodes.AccessDenied,
                    "Client IP address is not permitted to access administrative endpoints.",
                    context);
                await context.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted)
                    .ConfigureAwait(false);
                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}
