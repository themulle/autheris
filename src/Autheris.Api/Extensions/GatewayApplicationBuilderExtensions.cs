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
using Autheris.Domain.Audit;

namespace Autheris.Api.Extensions;

public static class GatewayApplicationBuilderExtensions
{
    public static WebApplication UseGatewayPipeline(this WebApplication app, GatewayOptions gatewayOptions)
    {
        // SR-L-02 / G-4: outside Development the host allow-list is mandatory (fail-closed).
        Autheris.Api.Extensions.DependencyInjection.GatewayStartupValidator.ValidateAllowedHosts(gatewayOptions, app.Environment);
        var hostAllowList = Autheris.Api.Security.HostAllowList.Resolve(gatewayOptions.Hosting, app.Environment.IsDevelopment());

        // O9: first middleware, so it catches everything below it. In Development ASP.NET Core puts the developer
        // exception page in front of the pipeline; it never sees an exception because this handler answers first.
        app.UseExceptionHandler();

        // Capture physical TCP remote IP before UseForwardedHeaders() overrides it with X-Forwarded-For, and resolve correlation ID.
        app.UseMiddleware<CorrelationIdMiddleware>();

        if (gatewayOptions.ReverseProxy.Enabled)
        {
            // A connection without a remote address (Unix domain socket, in-process host) cannot be matched against
            // KnownProxies/KnownNetworks, and ForwardedHeadersMiddleware would accept its X-Forwarded-* headers.
            // Such a peer is not a known proxy: drop the headers so they cannot spoof host, scheme or client IP.
            app.UseMiddleware<UntrustedProxyHeaderStrippingMiddleware>();
            app.UseForwardedHeaders();
        }

        // SR-L-02: validate Host (the forwarded host when the reverse proxy is enabled) before CORS/CSRF.
        app.UseMiddleware<HostAllowListMiddleware>(hostAllowList);
        app.UseCors();

        // SEC C-1 Edge Stripping: Strip any client-supplied x-autheris-* headers from untrusted clients
        app.UseMiddleware<EdgeHeaderStrippingMiddleware>();

        // WP-ARC-C-04 / ARC-A-10: Rewrite /graphql/{domain} to /graphql and set DomainScope + X-Autheris-Domain
        // BEFORE CSRF, authentication, authorization, rate limiting, and audit middlewares run.
        app.UseMiddleware<DomainScopeRewriteMiddleware>(gatewayOptions);

        // HTTP Security Response Headers (MED-01), OData v4 Protocol Compliance, and Dev Insecure Mode banner
        app.UseMiddleware<SecurityHeadersMiddleware>(gatewayOptions, app.Environment);

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
            app.UseHttpsRedirection();
        }

        // SEC M-05: MCP JSON-RPC POSTs must be sent as application/json.
        app.UseMiddleware<StrictMcpGuardMiddleware>(gatewayOptions);

        // Anti-CSRF Middleware: Enforce custom preflight header and Origin validation on GraphQL and state-changing REST requests
        app.UseMiddleware<GatewayCorsCsrfMiddleware>(gatewayOptions, hostAllowList, app.Environment);

        app.UseHttpMetrics();
        if (DevFeatures.Resolve(gatewayOptions, app.Environment.IsDevelopment()).VerboseErrors)
        {
            // F-AUTH-DX: bodyless 403s get a diagnostic problem+json (never outside Development)
            app.UseMiddleware<DevForbiddenDiagnosticsMiddleware>();
        }

        app.UseMiddleware<AccessAuditMiddleware>();
        app.UseMiddleware<PreAuthIpRateLimitingMiddleware>();
        app.UseAuthentication();
        // SR-M-02: IP-limit Bearer-exempted but unauthenticated requests before authorization can short-circuit with 401/403
        app.UseMiddleware<UnauthenticatedBearerIpRateLimitingMiddleware>();
        app.UseMiddleware<TokenRevocationMiddleware>();
        // F-AUTH-DX: issue the session cookie after a Basic login (no-op unless BasicAuth.Session is allowed)
        app.UseMiddleware<BasicAuthSessionMiddleware>();
        // ADR-017 / K-K10: Canonicalize AD Windows SIDs, OIDC claims, client certs, and GatewayRoles before authorization
        app.UseMiddleware<ClaimsNormalizationMiddleware>();
        // Finding 3.3 / R13: read-only agent and app tokens may only reach the query endpoints
        app.UseMiddleware<ReadOnlyTokenMiddleware>(
            new PathString(ResolveGraphQlPath(gatewayOptions)),
            new PathString(ResolveMcpBasePath(gatewayOptions)));
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

    internal static string ResolveGraphQlPath(GatewayOptions gatewayOptions) =>
        gatewayOptions.GraphQL.EndpointPath.StartsWith('/')
            ? gatewayOptions.GraphQL.EndpointPath
            : "/" + gatewayOptions.GraphQL.EndpointPath;

    internal static string ResolveMcpBasePath(GatewayOptions gatewayOptions) =>
        McpPathResolver.Resolve(gatewayOptions);

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
        app.MapMetrics().RequireAuthorization(GatewayPolicies.MetricsScrape).WithAuditExemption("Prometheus scrape endpoint");

        // 1. Health & Readiness Probes
        app.MapHealthEndpoints(gatewayOptions, app.Environment);

        // 2. Core GraphQL Engine & Dynamic Plugins
        var endpoint = ResolveGraphQlPath(gatewayOptions);

        var pluginManager = app.Services.GetService<Autheris.Application.Plugins.IPluginManager>();
        if (pluginManager != null && !string.IsNullOrWhiteSpace(gatewayOptions.Plugins.Directory))
        {
            pluginManager.LoadPluginsFromDirectory(gatewayOptions.Plugins.Directory);
        }

        app.UseWebSockets();
        // GraphQL API endpoint: Nitro (Banana Cake Pop) UI is disabled on /graphql so it serves strictly as
        // a headless, deterministic machine-to-machine API without HTML multiplexing.
        var gqlEndpoint = app.MapGraphQL(endpoint)
            .WithOptions((NitroAppOptions nitro) => nitro.Enable = false)
            .WithAudit(AuditLevel.Delegated, AuditEventTypes.TableQuery);
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
                })
                .WithAuditExemption("Embedded developer tool UI");

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
        app.MapDataContractEndpoints();
        app.MapDocIngestionEndpoints();
        app.MapGovernanceClassificationEndpoints();
        app.MapCatalogEndpoints();
        app.MapSystemEndpoints();
        if (gatewayOptions.OData.Enabled)
        {
            app.MapODataEndpoints(gatewayOptions);
        }
        app.MapMcpEndpoints(gatewayOptions);
        app.MapSchemaRegistryEndpoints();
        app.MapBackstageEndpoints(gatewayOptions);
        app.MapHitLEndpoints();
        app.MapGovernanceApiEndpoints();
        app.MapGovernancePlanEndpoints();
        app.MapTokenRevocationEndpoints(); // SEC M-14 (GAP-B)
        app.MapTenantRegistryAdminEndpoints(); // M-A2: operator resolution of a tenant id collision
        app.MapFinOpsEndpoints();
        app.MapRebacEndpoints();
        app.MapVirtualFilterEndpoints();
        if (gatewayOptions.Arrow.Enabled)
        {
            app.MapArrowExportEndpoints();
        }
        if (gatewayOptions.Arrow.Enabled && gatewayOptions.Arrow.EnableFlightSql)
        {
            app.MapArrowFlightSqlEndpoints();
        }
        if (gatewayOptions.DuckDbOlap.Enabled)
        {
            app.MapDuckDbOlapEndpoints();
        }
        if (gatewayOptions.Lakehouse.Enabled)
        {
            app.MapIcebergRestCatalogEndpoints();
        }
        app.MapEnvoyExtAuthzEndpoints();
        app.MapWebSqlEndpoints(gatewayOptions);
        app.MapDatasetDataEndpoints();
        app.MapSqlEndpoints(gatewayOptions);
        app.MapProcedureEndpoints(gatewayOptions);
        app.MapDevPortalEndpoints(gatewayOptions);
        app.MapDevEndpoints(gatewayOptions); // F-AUTH-DX: Development only (no routes elsewhere)
        app.MapPlan9Endpoints();

        LoadSqlEndpoints(app.Services, gatewayOptions);

        return app;
    }

    /// <summary>
    /// Loads declarative SQL endpoints. Fail-closed: when enabled, a missing loader registration aborts startup.
    /// </summary>
    public static void LoadSqlEndpoints(IServiceProvider services, GatewayOptions gatewayOptions)
    {
        if (!gatewayOptions.SqlEndpoints.Enabled)
        {
            return;
        }

        var loader = services.GetRequiredService<Autheris.Application.SqlEndpoints.Services.SqlEndpointLoader>();
        loader.LoadFromDirectory(gatewayOptions.SqlEndpoints.Directory, gatewayOptions.SqlEndpoints.EnableHotReload);
    }
}
