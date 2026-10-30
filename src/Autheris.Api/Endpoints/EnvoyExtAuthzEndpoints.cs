namespace Autheris.Api.Endpoints;

using Autheris.Api.Errors;
using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Mesh.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

/// <summary>
/// F-ARCH-11: Envoy External Authorization (ext_authz) endpoints and Istio CRD manifest exporter.
/// </summary>
public static class EnvoyExtAuthzEndpoints
{
    public static IEndpointRouteBuilder MapEnvoyExtAuthzEndpoints(this IEndpointRouteBuilder app)
    {
        // API-2: the former JSON protocol endpoint (POST /api/v1/envoy/authz) decided with the identity of the calling
        // mesh service instead of the end user (confused deputy) and is no longer mapped. The header mode below
        // receives the original request, so the decision uses the end user's credentials.

        // GET or POST /api/v1/envoy/check: Envoy HTTP ext_authz header mode
        // G3: Envoy http_service forwards the original method and appends the original path to path_prefix, so the route
        // is a catch-all over every method; the suffix is the original request path.
        app.MapMethods("/api/v1/envoy/check/{**originalPath}", new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }, async (
            HttpContext context,
            IEnvoyExtAuthzService authzService,
            IOptions<GatewayOptions> gatewayOptions) =>
        {
            var (method, path) = ResolveOriginalRequest(context, gatewayOptions.Value.EnvoyExtAuthz);

            var headerMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in context.Request.Headers)
            {
                if (v.Count > 0 && v[0] != null)
                {
                    headerMap[k] = v[0]!;
                }
            }

            var decision = await authzService.CheckHttpAsync(method, path, headerMap, context.User, context.RequestAborted);

            if (decision.Status.Code == 0)
            {
                // Allowed (HTTP 200)
                if (decision.HttpResponse.OkResponse?.Headers != null)
                {
                    foreach (var h in decision.HttpResponse.OkResponse.Headers)
                    {
                        context.Response.Headers.TryAdd(h.Header.Key, h.Header.Value);
                    }
                }
                return Results.Ok();
            }

            // Denied (HTTP 403 or 401)
            var statusCode = decision.HttpResponse.DeniedResponse?.Status.Code ?? 403;
            if (decision.HttpResponse.DeniedResponse?.Headers != null)
            {
                foreach (var h in decision.HttpResponse.DeniedResponse.Headers)
                {
                    context.Response.Headers.TryAdd(h.Header.Key, h.Header.Value);
                }
            }

            // NL-SC-22: the PDP text passes the control-character strip and length cap before it is echoed; the protocol shape stays.
            var safeBody = ApiProblems.SanitizeOperatorText(decision.HttpResponse.DeniedResponse?.Body, "Access Denied by Autheris PDP");
            return Results.Json(new { error = safeBody, status = statusCode }, statusCode: statusCode);
        }).RequireAuthorization().WithAudit(AuditLevel.Full, AuditEventTypes.AuthSucceeded);

        // GET /api/v1/envoy/export/envoyfilter.yaml: Export Istio EnvoyFilter CRD
        app.MapGet("/api/v1/envoy/export/envoyfilter.yaml", (
            IEnvoyExtAuthzService authzService,
            string? @namespace,
            string? host,
            int? port) =>
        {
            var options = new EnvoyFilterExportOptions
            {
                MeshNamespace = @namespace ?? "istio-system",
                ServiceHost = host ?? "autheris-gateway.autheris.svc.cluster.local",
                ServicePort = port ?? 8080
            };

            try
            {
                var yaml = authzService.GenerateIstioEnvoyFilterYaml(options);
                return Results.Text(yaml, "text/yaml; charset=utf-8");
            }
            catch (ArgumentException)
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Invalid export parameters (namespace/host must be DNS names, port numeric).");
            }
        }).WithAudit(AuditLevel.Summarized, AuditEventTypes.MetadataExport);

        // GET /api/v1/envoy/export/wasmplugin.yaml: Export Istio WasmPlugin CRD
        app.MapGet("/api/v1/envoy/export/wasmplugin.yaml", (
            IEnvoyExtAuthzService authzService,
            string? @namespace,
            string? host,
            int? port) =>
        {
            var options = new EnvoyFilterExportOptions
            {
                MeshNamespace = @namespace ?? "istio-system",
                ServiceHost = host ?? "autheris-gateway.autheris.svc.cluster.local",
                ServicePort = port ?? 8080
            };

            try
            {
                var yaml = authzService.GenerateIstioWasmPluginYaml(options);
                return Results.Text(yaml, "text/yaml; charset=utf-8");
            }
            catch (ArgumentException)
            {
                return ApiProblems.BadRequest(ApiErrorCodes.BadRequest, "Invalid export parameters (namespace/host must be DNS names, port numeric).");
            }
        }).WithAudit(AuditLevel.Summarized, AuditEventTypes.MetadataExport);

        return app;
    }

    /// <summary>
    /// Security review: <c>X-Original-Method</c> / <c>X-Original-URI</c> describe the request being authorised and are
    /// only honoured when the raw TCP peer (captured before forwarded headers are applied) is in the dedicated
    /// <see cref="EnvoyExtAuthzOptions"/> allowlist, not the shared ForwardAuth/ReverseProxy trusted proxies. Empty
    /// allowlist (default) = never trusted. Any other caller is decided on the route suffix and the real method, so it
    /// cannot obtain an ALLOW for a different (e.g. read-only) request than the one it sends.
    /// </summary>
    internal static (string Method, string Path) ResolveOriginalRequest(HttpContext context, EnvoyExtAuthzOptions? options)
    {
        var trustedPeer = options != null && options.IsTrustedPeer(TrustedProxyPeer.GetRawRemoteIp(context));

        var method = trustedPeer && context.Request.Headers.TryGetValue("X-Original-Method", out var om) && om.Count > 0 && !string.IsNullOrWhiteSpace(om[0])
            ? om[0]!
            : context.Request.Method;

        var path = trustedPeer && context.Request.Headers.TryGetValue("X-Original-URI", out var ou) && ou.Count > 0 && !string.IsNullOrWhiteSpace(ou[0])
            ? ou[0]!
            : (context.Request.RouteValues.TryGetValue("originalPath", out var op) && op is string suffix && suffix.Length > 0
                ? "/" + suffix + context.Request.QueryString.Value
                : context.Request.Path.Value ?? "/");

        return (method, path);
    }
}
