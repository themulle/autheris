namespace Autheris.Api.Middleware;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Anti-CSRF and CORS preflight/origin validation middleware for GraphQL and state-changing REST requests (WP-ARC-C-04).
/// </summary>
public sealed class GatewayCorsCsrfMiddleware
{
    private readonly RequestDelegate _next;
    private readonly GatewayOptions _gatewayOptions;
    private readonly HostAllowList _hostAllowList;
    private readonly IHostEnvironment _env;
    private readonly string _endpoint;

    public GatewayCorsCsrfMiddleware(
        RequestDelegate next,
        GatewayOptions gatewayOptions,
        HostAllowList hostAllowList,
        IHostEnvironment env)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _hostAllowList = hostAllowList ?? throw new ArgumentNullException(nameof(hostAllowList));
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _endpoint = GatewayApplicationBuilderExtensions.ResolveGraphQlPath(gatewayOptions);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_gatewayOptions.IsAllCorsAllowed)
        {
            if (context.Request.Headers.TryGetValue("Origin", out var originVal))
            {
                context.Response.Headers.AccessControlAllowOrigin = originVal;
                context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
                context.Response.Headers.AccessControlAllowHeaders = "*";
            }

            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                return;
            }
        }

        bool isWebSocketUpgrade = context.WebSockets.IsWebSocketRequest ||
                                  string.Equals(context.Request.Headers["Upgrade"], "websocket", StringComparison.OrdinalIgnoreCase);

        bool isGraphQLEndpoint = (HttpMethods.IsPost(context.Request.Method) ||
             (HttpMethods.IsGet(context.Request.Method) && (context.Request.Query.ContainsKey("query") || isWebSocketUpgrade)))
            && context.Request.Path.StartsWithSegments(_endpoint);

        bool isStateChangingMethod = HttpMethods.IsPost(context.Request.Method) ||
                                     HttpMethods.IsPut(context.Request.Method) ||
                                     HttpMethods.IsDelete(context.Request.Method) ||
                                     HttpMethods.IsPatch(context.Request.Method);

        bool isStateChangingRestEndpoint = isStateChangingMethod && !isGraphQLEndpoint;

        if (isGraphQLEndpoint || isStateChangingRestEndpoint)
        {
            // For REST endpoints, CSRF attack vectors require browser execution with ambient credentials (Cookie, cached Basic/Negotiate)
            // indicated by Cookie, Sec-Fetch-*, or Origin/Referer headers.
            // Exclude explicit login exchange (/api/auth/login) unless ambient Cookie is present.
            bool isLoginEndpoint = context.Request.Path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase);
            bool hasBrowserIndicators = context.Request.Headers.ContainsKey("Cookie") ||
                                        context.Request.Headers.ContainsKey("Origin") ||
                                        context.Request.Headers.ContainsKey("Referer") ||
                                        context.Request.Headers.ContainsKey("Sec-Fetch-Site");

            bool requiresCsrfProtection = (isGraphQLEndpoint && !isWebSocketUpgrade) || (!isLoginEndpoint && hasBrowserIndicators);

            if (requiresCsrfProtection)
            {
                bool hasPreflightHeader = context.Request.Headers.ContainsKey("GraphQL-Preflight") ||
                                          context.Request.Headers.ContainsKey("X-Requested-With") ||
                                          context.Request.Headers.ContainsKey("X-CSRF-Token") ||
                                          context.Request.Headers.ContainsKey("X-Trino-User");

                if (!hasPreflightHeader)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    var endpointType = isGraphQLEndpoint ? "GraphQL" : "REST";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = $"CSRF Protection: Missing custom preflight header ('GraphQL-Preflight: 1', 'X-Requested-With', or 'X-CSRF-Token') on {endpointType} request."
                    }, context.RequestAborted).ConfigureAwait(false);
                    return;
                }
            }

            // Origin / Referer validation
            string? originHeader = context.Request.Headers.Origin.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(originHeader))
            {
                var refererHeader = context.Request.Headers.Referer.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(refererHeader) && Uri.TryCreate(refererHeader, UriKind.Absolute, out var refUri))
                {
                    originHeader = $"{refUri.Scheme}://{refUri.Authority}";
                }
            }

            if (!string.IsNullOrWhiteSpace(originHeader))
            {
                bool isOriginTrusted = _gatewayOptions.IsAllCorsAllowed;
                if (!isOriginTrusted && Uri.TryCreate(originHeader, UriKind.Absolute, out var originUri))
                {
                    // SR-L-02: the Origin must be on the host allow-list; Origin == Host alone is not proof of same-origin.
                    if (_hostAllowList.IsAllowed(originUri.Authority))
                    {
                        isOriginTrusted = true;
                    }
                    else
                    {
                        foreach (var trusted in _gatewayOptions.GraphQL.TrustedOrigins)
                        {
                            if (string.Equals(trusted, "*", StringComparison.OrdinalIgnoreCase))
                            {
                                if (_env.IsDevelopment())
                                {
                                    isOriginTrusted = true;
                                    break;
                                }
                                continue;
                            }
                            if (Uri.TryCreate(trusted, UriKind.Absolute, out var trustedUri))
                            {
                                if (string.Equals(trustedUri.Authority, originUri.Authority, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(trustedUri.Scheme, originUri.Scheme, StringComparison.OrdinalIgnoreCase))
                                {
                                    isOriginTrusted = true;
                                    break;
                                }
                            }
                            else if (string.Equals(trusted, originHeader, StringComparison.OrdinalIgnoreCase))
                            {
                                isOriginTrusted = true;
                                break;
                            }
                        }
                    }
                }

                if (!isOriginTrusted)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = $"CSRF Protection: Request Origin '{originHeader}' is not trusted."
                    }, context.RequestAborted).ConfigureAwait(false);
                    return;
                }
            }
            else if (!_gatewayOptions.IsAllCorsAllowed && _gatewayOptions.GraphQL.TrustedOrigins.Count > 0 && !_env.IsDevelopment())
            {
                // If browser-originating request omits Origin/Referer in production with trusted origins configured, reject
                var secFetchSite = context.Request.Headers["Sec-Fetch-Site"].FirstOrDefault();
                if (!string.IsNullOrEmpty(secFetchSite) && !string.Equals(secFetchSite, "none", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "CSRF Protection: Origin or Referer header required for browser requests."
                    }, context.RequestAborted).ConfigureAwait(false);
                    return;
                }
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}
