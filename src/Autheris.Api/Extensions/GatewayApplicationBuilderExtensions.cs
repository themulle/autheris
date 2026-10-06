using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Prometheus;
using System;
using System.Linq;
using ChilliCream.Nitro.App;

namespace Autheris.Api.Extensions;

public static class GatewayApplicationBuilderExtensions
{
    public static WebApplication UseGatewayPipeline(this WebApplication app, GatewayOptions gatewayOptions)
    {
        // Capture physical TCP remote IP before UseForwardedHeaders() overrides it with X-Forwarded-For
        app.Use(async (context, next) =>
        {
            if (context.Connection.RemoteIpAddress != null)
            {
                context.Items["OriginalTcpRemoteIp"] = context.Connection.RemoteIpAddress;
            }
            await next();
        });

        if (gatewayOptions.ReverseProxy.Enabled)
        {
            app.UseForwardedHeaders();
        }
        app.UseCors();

        // SEC C-1 Edge Stripping: Strip any client-supplied x-autheris-* headers from untrusted clients
        app.Use(async (context, next) =>
        {
            var headersToRemove = context.Request.Headers.Keys
                .Where(k => k.StartsWith("x-autheris-", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var h in headersToRemove)
            {
                context.Request.Headers.Remove(h);
            }
            await next();
        });

        // HTTP Security Response Headers (MED-01)
        // The embedded Nitro (Banana Cake Pop) UI needs inline scripts/styles, blob: workers and data: assets, which the
        // strict default policy blocks (blank page). Only in Development and only below the Banana Cake Pop endpoint path
        // (/ui/bcp) a relaxed policy is used; every other path (including /graphql) keeps the strict one.
        const string StrictCsp = "default-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self';";
        const string NitroToolCsp = "default-src 'self'; script-src 'self' 'unsafe-inline' 'unsafe-eval' blob:; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; worker-src 'self' blob:; connect-src 'self'; manifest-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self';";
        var nitroToolPath = gatewayOptions.GraphQL.BananaCakePopPath.StartsWith('/')
            ? gatewayOptions.GraphQL.BananaCakePopPath
            : "/" + gatewayOptions.GraphQL.BananaCakePopPath;
        var allowNitroToolCsp = app.Environment.IsDevelopment() && gatewayOptions.GraphQL.EnableBananaCakePop;
        app.Use(async (context, next) =>
        {
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            context.Response.Headers.Append("X-Frame-Options", "DENY");
            context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
            context.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
            var isToolPath = allowNitroToolCsp
                && context.Request.Path.StartsWithSegments(nitroToolPath, StringComparison.OrdinalIgnoreCase);
            context.Response.Headers.Append("Content-Security-Policy", isToolPath ? NitroToolCsp : StrictCsp);
            await next();
        });

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
            app.UseHttpsRedirection();
        }

        var devSecurity = DevFeatures.Resolve(gatewayOptions, app.Environment.IsDevelopment()).DevSecurity;
        if (app.Environment.IsDevelopment() && (gatewayOptions.HasAnySecurityBypassActive || devSecurity.Count > 0))
        {
            var insecureModeHeader = string.Join("; ",
                gatewayOptions.GetAllActiveBypasses().Concat(devSecurity.Select(s => "DEV-SECURITY:" + s)));
            app.Use(async (context, next) =>
            {
                context.Response.Headers.Append("X-Gateway-Insecure-Mode", insecureModeHeader);
                await next();
            });
        }

        var endpoint = gatewayOptions.GraphQL.EndpointPath.StartsWith('/')
            ? gatewayOptions.GraphQL.EndpointPath
            : "/" + gatewayOptions.GraphQL.EndpointPath;

        var mcpBasePath = ResolveMcpBasePath(gatewayOptions);

        // SEC M-05: MCP JSON-RPC POSTs must be sent as application/json. text/plain & form encodings are
        // "simple requests" that browsers send cross-site without preflight (ambient Negotiate/Kerberos credentials).
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method) &&
                context.Request.Path.StartsWithSegments(mcpBasePath) &&
                !IsJsonContentType(context.Request.ContentType))
            {
                context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "MCP requests must use 'Content-Type: application/json'."
                });
                return;
            }

            await next();
        });

        // Anti-CSRF Middleware: Enforce custom preflight header on GraphQL POST and GET query requests
        app.Use(async (context, next) =>
        {
            if (gatewayOptions.IsAllCorsAllowed)
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
                && context.Request.Path.StartsWithSegments(endpoint);

            bool isStateChangingRestEndpoint = (HttpMethods.IsPost(context.Request.Method) ||
                                                HttpMethods.IsPut(context.Request.Method) ||
                                                HttpMethods.IsDelete(context.Request.Method) ||
                                                HttpMethods.IsPatch(context.Request.Method))
                                               && (context.Request.Path.StartsWithSegments("/api") ||
                                                   context.Request.Path.StartsWithSegments("/odata") ||
                                                   // SEC M-05: MCP is covered by the CSRF protection as well
                                                   context.Request.Path.StartsWithSegments(mcpBasePath));

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
                                              context.Request.Headers.ContainsKey("X-CSRF-Token");

                    if (!hasPreflightHeader)
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        var endpointType = isGraphQLEndpoint ? "GraphQL" : "REST";
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = $"CSRF Protection: Missing custom preflight header ('GraphQL-Preflight: 1', 'X-Requested-With', or 'X-CSRF-Token') on {endpointType} request."
                        });
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
                    bool isOriginTrusted = gatewayOptions.IsAllCorsAllowed;
                    if (!isOriginTrusted && Uri.TryCreate(originHeader, UriKind.Absolute, out var originUri))
                    {
                        if (string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
                        {
                            isOriginTrusted = true;
                        }
                        else
                        {
                            foreach (var trusted in gatewayOptions.GraphQL.TrustedOrigins)
                            {
                                if (string.Equals(trusted, "*", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (app.Environment.IsDevelopment())
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
                        });
                        return;
                    }
                }
                else if (!gatewayOptions.IsAllCorsAllowed && gatewayOptions.GraphQL.TrustedOrigins.Count > 0 && !app.Environment.IsDevelopment())
                {
                    // If browser-originating request omits Origin/Referer in production with trusted origins configured, reject
                    var secFetchSite = context.Request.Headers["Sec-Fetch-Site"].FirstOrDefault();
                    if (!string.IsNullOrEmpty(secFetchSite) && !string.Equals(secFetchSite, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            error = "CSRF Protection: Origin or Referer header required for browser requests."
                        });
                        return;
                    }
                }
            }

            await next();
        });

        app.UseHttpMetrics();
        if (DevFeatures.Resolve(gatewayOptions, app.Environment.IsDevelopment()).VerboseErrors)
        {
            // F-AUTH-DX: bodyless 403s get a diagnostic problem+json (never outside Development)
            app.UseMiddleware<DevForbiddenDiagnosticsMiddleware>();
        }

        app.UseMiddleware<PreAuthIpRateLimitingMiddleware>();
        app.UseAuthentication();
        app.UseMiddleware<TokenRevocationMiddleware>();
        // F-AUTH-DX: issue the session cookie after a Basic login (no-op unless BasicAuth.Session is allowed)
        app.UseMiddleware<BasicAuthSessionMiddleware>();
        app.UseAuthorization();
        app.UseMiddleware<PostAuthSidRateLimitingMiddleware>();
        app.UseMiddleware<SecurityContextResolutionMiddleware>();
        app.UseMiddleware<ResourceGroupMiddleware>();
        app.UseMiddleware<FinOpsBudgetMiddleware>();
        app.UseMiddleware<SchemaContractMiddleware>();
        app.UseMiddleware<IncrementalDeliveryMiddleware>();
        app.UseMiddleware<TrafficShadowingMiddleware>();
        app.UseMiddleware<OpenTelemetryTracingMiddleware>();
        // F-DATA-01: Parquet output negotiation is the outermost output transformation around the governed JSON
        // (egress interceptors / audit below still operate on JSON).
        app.UseMiddleware<ParquetGraphQLResponseMiddleware>();
        app.UseMiddleware<GatewayExtensibilityMiddleware>();

        return app;
    }

    internal static string ResolveMcpBasePath(GatewayOptions gatewayOptions)
    {
        var path = string.IsNullOrWhiteSpace(gatewayOptions.Mcp.EndpointPath)
            ? "/mcp"
            : gatewayOptions.Mcp.EndpointPath.TrimEnd('/');
        if (path.Length == 0)
        {
            path = "/mcp";
        }
        return path.StartsWith('/') ? path : "/" + path;
    }

    internal static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var mediaType = contentType.Split(';', 2)[0].Trim();
        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    public static WebApplication MapGatewayEndpoints(this WebApplication app, GatewayOptions gatewayOptions)
    {
        app.MapMetrics().RequireAuthorization();

        // 1. Health & Readiness Probes
        app.MapHealthEndpoints(gatewayOptions, app.Environment);

        // 2. Core GraphQL Engine & Dynamic Plugins
        var endpoint = gatewayOptions.GraphQL.EndpointPath.StartsWith('/')
            ? gatewayOptions.GraphQL.EndpointPath
            : "/" + gatewayOptions.GraphQL.EndpointPath;

        var pluginManager = app.Services.GetService<Autheris.Application.Plugins.IPluginManager>();
        if (pluginManager != null && !string.IsNullOrWhiteSpace(gatewayOptions.Plugins.Directory))
        {
            pluginManager.LoadPluginsFromDirectory(gatewayOptions.Plugins.Directory);
        }

        // Domain-scoped GraphQL endpoint: /graphql/{domain} rewrites path to /graphql and sets DomainScope
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value;
            if (!string.IsNullOrEmpty(path) && path.StartsWith(endpoint + "/", StringComparison.OrdinalIgnoreCase))
            {
                var subPath = path[(endpoint.Length + 1)..].Trim('/');
                if (!string.IsNullOrEmpty(subPath) && !subPath.Contains('/'))
                {
                    context.Items["DomainScope"] = subPath;
                    context.Request.Path = endpoint;
                }
            }
            await next(context);
        });

        app.UseWebSockets();
        // GraphQL API endpoint: Nitro (Banana Cake Pop) UI is disabled on /graphql so it serves strictly as
        // a headless, deterministic machine-to-machine API without HTML multiplexing.
        var gqlEndpoint = app.MapGraphQL(endpoint)
            .WithOptions((NitroAppOptions nitro) => nitro.Enable = false);
        // SEC H-02: OpenSchema no longer opens /graphql; only the Development-only anonymous mode does.
        // (SEC M-03: with the authenticated-user FallbackPolicy the anonymous mode must opt out explicitly.)
        if (gatewayOptions.IsAnonymousAccessAllowed)
        {
            gqlEndpoint.AllowAnonymous();
        }
        else
        {
            gqlEndpoint.RequireAuthorization();
        }

        // Dedicated Banana Cake Pop endpoint for development and staging (/ui/bcp).
        // In production, administrators and developers use the standalone Banana Cake Pop desktop app.
        if (gatewayOptions.GraphQL.EnableBananaCakePop)
        {
            var bcpPath = gatewayOptions.GraphQL.BananaCakePopPath.StartsWith('/')
                ? gatewayOptions.GraphQL.BananaCakePopPath
                : "/" + gatewayOptions.GraphQL.BananaCakePopPath;

            var bcpEndpoint = app.MapNitroApp(bcpPath, endpoint)
                .WithOptions((NitroAppOptions nitro) =>
                {
                    nitro.ServeMode = ServeMode.Embedded;
                    nitro.GraphQLEndpoint = endpoint;
                    // /graphql enforces a CSRF preflight header (see the CSRF middleware above); Nitro sends none by default.
                    nitro.HttpHeaders = new HeaderDictionary { ["GraphQL-Preflight"] = "1" };
                    // The strict CSP (connect-src 'self') blocks the vendor telemetry calls anyway; do not attempt them.
                    nitro.DisableTelemetry = true;
                    nitro.Title = "Autheris GraphQL";
                    nitro.Document = "{\n  catalog(first: 5) {\n    domain\n    schema\n    tableName\n    sensitivity\n  }\n}\n";
                });

            if (gatewayOptions.IsAnonymousAccessAllowed)
            {
                bcpEndpoint.AllowAnonymous();
            }
            else
            {
                bcpEndpoint.RequireAuthorization();
            }
        }

        // 3. Modular Feature Endpoints (Route Groups)
        app.MapAuthEndpoints();
        app.MapWebhookEndpoints(gatewayOptions);
        app.MapStreamingCdcEndpoints();
        app.MapDbtEndpoints();
        app.MapGovernanceEndpoints();
        app.MapSystemEndpoints();
        app.MapODataEndpoints(gatewayOptions);
        app.MapMcpEndpoints(gatewayOptions);
        app.MapSchemaRegistryEndpoints();
        app.MapBackstageEndpoints(gatewayOptions);
        app.MapHitLEndpoints();
        app.MapTokenRevocationEndpoints(); // SEC M-14 (GAP-B)
        app.MapFinOpsEndpoints();
        app.MapRebacEndpoints();
        app.MapArrowExportEndpoints();
        app.MapArrowFlightSqlEndpoints();
        app.MapDuckDbOlapEndpoints();
        app.MapIcebergRestCatalogEndpoints();
        app.MapEnvoyExtAuthzEndpoints();
        app.MapWebSqlEndpoints();
        app.MapSqlEndpoints(gatewayOptions);
        app.MapProcedureEndpoints(gatewayOptions);
        app.MapDevPortalEndpoints(gatewayOptions);
        app.MapDevEndpoints(gatewayOptions); // F-AUTH-DX: Development only (no routes elsewhere)

        if (gatewayOptions.SqlEndpoints.Enabled)
        {
            var loader = app.Services.GetService<Autheris.Application.SqlEndpoints.Services.SqlEndpointLoader>();
            loader?.LoadFromDirectory(gatewayOptions.SqlEndpoints.Directory, gatewayOptions.SqlEndpoints.EnableHotReload);
        }

        return app;
    }
}
