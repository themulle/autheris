namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Mesh.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// F-ARCH-11: Envoy External Authorization (ext_authz) endpoints and Istio CRD manifest exporter.
/// </summary>
public static class EnvoyExtAuthzEndpoints
{
    public static IEndpointRouteBuilder MapEnvoyExtAuthzEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /api/v1/envoy/authz: JSON protocol (envoy.service.auth.v3.CheckRequest -> CheckResponse)
        app.MapPost("/api/v1/envoy/authz", async (
            EnvoyCheckRequest request,
            IEnvoyExtAuthzService authzService,
            HttpContext context) =>
        {
            var response = await authzService.CheckAsync(request, context.User, context.RequestAborted);
            return Results.Ok(response);
        }).RequireAuthorization();

        // GET or POST /api/v1/envoy/check: Envoy HTTP ext_authz header mode
        app.MapMethods("/api/v1/envoy/check", new[] { "GET", "POST" }, async (
            HttpContext context,
            IEnvoyExtAuthzService authzService) =>
        {
            var method = context.Request.Headers.TryGetValue("X-Original-Method", out var om) && om.Count > 0
                ? om[0]!
                : context.Request.Method;

            var path = context.Request.Headers.TryGetValue("X-Original-URI", out var ou) && ou.Count > 0
                ? ou[0]!
                : context.Request.Path.Value ?? "/";

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

            var body = decision.HttpResponse.DeniedResponse?.Body ?? "Access Denied by Autheris PDP";
            return Results.Json(new { error = body, status = statusCode }, statusCode: statusCode);
        }).RequireAuthorization();

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

            var yaml = authzService.GenerateIstioEnvoyFilterYaml(options);
            return Results.Text(yaml, "text/yaml; charset=utf-8");
        });

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

            var yaml = authzService.GenerateIstioWasmPluginYaml(options);
            return Results.Text(yaml, "text/yaml; charset=utf-8");
        });

        return app;
    }
}
