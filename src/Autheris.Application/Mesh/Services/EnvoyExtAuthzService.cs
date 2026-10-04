namespace Autheris.Application.Mesh.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Autheris.Application.Interfaces;
using Autheris.Application.Mesh.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// F-ARCH-11: Envoy External Authorization (ext_authz) & Istio service mesh adapter.
/// Connects Envoy HTTP filters / Wasm plugins to Autheris PDP for zero-trust authorization.
/// </summary>
public sealed class EnvoyExtAuthzService : IEnvoyExtAuthzService
{
    private readonly IPolicyEnforcementService _policyService;
    private readonly ILogger<EnvoyExtAuthzService> _logger;

    private static readonly char[] CrlfChars = new[] { '\r', '\n' };

    public EnvoyExtAuthzService(
        IPolicyEnforcementService policyService,
        ILogger<EnvoyExtAuthzService> logger)
    {
        _policyService = policyService ?? throw new ArgumentNullException(nameof(policyService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<EnvoyCheckResponse> CheckAsync(EnvoyCheckRequest request, CancellationToken ct = default)
    {
        try
        {
            if (request?.Attributes?.Request?.Http == null)
            {
                _logger.LogWarning("Envoy ext_authz check rejected: missing HTTP request attributes.");
                return EnvoyCheckResponse.Deny(400, "Bad Request: Missing HTTP attributes in Envoy check payload.");
            }

            var http = request.Attributes.Request.Http;
            var contextExtensions = request.Attributes.ContextExtensions ?? new Dictionary<string, string>();
            var clientIpStr = request.Attributes.Source?.Address?.SocketAddress?.Address;
            var sourcePrincipal = request.Attributes.Source?.Principal;

            return await EvaluateInternalAsync(
                http.Method,
                http.Path,
                http.Headers,
                contextExtensions,
                clientIpStr,
                sourcePrincipal,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fail-closed: unexpected error during Envoy ext_authz evaluation.");
            return EnvoyCheckResponse.Deny(500, "Internal Server Error: Authorization check failed.");
        }
    }

    public async ValueTask<EnvoyCheckResponse> CheckHttpAsync(
        string method,
        string path,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken ct = default)
    {
        try
        {
            return await EvaluateInternalAsync(
                method,
                path,
                headers,
                new Dictionary<string, string>(),
                null,
                null,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fail-closed: unexpected error during HTTP ext_authz check.");
            return EnvoyCheckResponse.Deny(500, "Internal Server Error: Authorization check failed.");
        }
    }

    private async ValueTask<EnvoyCheckResponse> EvaluateInternalAsync(
        string method,
        string rawPath,
        IReadOnlyDictionary<string, string>? headers,
        IReadOnlyDictionary<string, string> contextExtensions,
        string? clientIpStr,
        string? sourcePrincipal,
        CancellationToken ct)
    {
        var headerDict = headers != null
            ? new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Security check: Guard against CRLF injection in input headers
        foreach (var (k, v) in headerDict)
        {
            if (k.IndexOfAny(CrlfChars) >= 0 || v.IndexOfAny(CrlfChars) >= 0)
            {
                _logger.LogWarning("CRLF injection detected in Envoy ext_authz header '{Key}'", k);
                return EnvoyCheckResponse.Deny(400, "Bad Request: Header smuggling / CRLF detected.");
            }
        }

        // 1. Path normalization and sanitization
        var normalizedPath = NormalizePath(rawPath);
        if (normalizedPath == null)
        {
            _logger.LogWarning("Path traversal or invalid path detected: {RawPath}", rawPath);
            return EnvoyCheckResponse.Deny(400, "Bad Request: Invalid or traversing path.");
        }

        // 2. Extract Tenant
        string tenantStr = "default";
        if (contextExtensions.TryGetValue("tenant", out var ctxTenant) && !string.IsNullOrWhiteSpace(ctxTenant))
        {
            tenantStr = ctxTenant;
        }
        else if (headerDict.TryGetValue("x-tenant-id", out var hTenant) && !string.IsNullOrWhiteSpace(hTenant))
        {
            tenantStr = hTenant;
        }
        else if (headerDict.TryGetValue("x-autheris-tenant", out var aTenant) && !string.IsNullOrWhiteSpace(aTenant))
        {
            tenantStr = aTenant;
        }

        // 3. Extract Principal (Subject)
        string? principal = null;
        if (!string.IsNullOrWhiteSpace(sourcePrincipal))
        {
            principal = sourcePrincipal;
        }
        else if (contextExtensions.TryGetValue("user", out var ctxUser) && !string.IsNullOrWhiteSpace(ctxUser))
        {
            principal = ctxUser;
        }
        else if (headerDict.TryGetValue("x-autheris-principal", out var hPrin) && !string.IsNullOrWhiteSpace(hPrin))
        {
            principal = hPrin;
        }
        else if (headerDict.TryGetValue("x-user-id", out var hUser) && !string.IsNullOrWhiteSpace(hUser))
        {
            principal = hUser;
        }
        else if (headerDict.TryGetValue("authorization", out var authHeader) && !string.IsNullOrWhiteSpace(authHeader))
        {
            principal = ExtractPrincipalFromAuthorizationHeader(authHeader, out var tokenTenant);
            if (!string.IsNullOrWhiteSpace(tokenTenant) && tenantStr == "default")
            {
                tenantStr = tokenTenant;
            }
        }

        if (string.IsNullOrWhiteSpace(principal))
        {
            principal = "anonymous";
        }

        // 4. Extract Groups / Roles
        var groupList = new List<Sid>();
        if (headerDict.TryGetValue("x-roles", out var rVal) && !string.IsNullOrWhiteSpace(rVal))
        {
            foreach (var r in rVal.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                groupList.Add(new Sid(r));
            }
        }

        // 5. Determine Resource / Table
        string resourceName = "default";
        if (contextExtensions.TryGetValue("table", out var ctxTable) && !string.IsNullOrWhiteSpace(ctxTable))
        {
            resourceName = ctxTable;
        }
        else if (contextExtensions.TryGetValue("resource", out var ctxRes) && !string.IsNullOrWhiteSpace(ctxRes))
        {
            resourceName = ctxRes;
        }
        else
        {
            resourceName = ExtractResourceFromPath(normalizedPath);
        }

        // Parse Client IP
        IPAddress clientIp = IPAddress.Loopback;
        if (!string.IsNullOrWhiteSpace(clientIpStr) && IPAddress.TryParse(clientIpStr, out var parsedIp))
        {
            clientIp = parsedIp;
        }
        else if (headerDict.TryGetValue("x-forwarded-for", out var xff) && !string.IsNullOrWhiteSpace(xff))
        {
            var firstIp = xff.Split(',')[0].Trim();
            if (IPAddress.TryParse(firstIp, out var parsedXff))
            {
                clientIp = parsedXff;
            }
        }

        // 6. Security Evaluation Context
        var tenantId = new TenantId(tenantStr);
        var targetTable = TableIdentifier.TryParse(resourceName, out var parsedTid)
            ? parsedTid
            : new TableIdentifier("default", "public", resourceName);
        var evalContext = new SecurityEvaluationContext(
            UserSid: new Sid(principal),
            GroupSids: groupList,
            Tenant: tenantId,
            TargetTable: targetTable,
            RequestedColumns: Array.Empty<string>(),
            ClientIp: clientIp,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: null,
            Attributes: new Dictionary<string, object?>
            {
                ["method"] = method?.ToUpperInvariant() ?? "GET",
                ["path"] = normalizedPath
            });

        // 7. Policy Evaluation
        var decision = await _policyService.EvaluatePolicyAsync(evalContext, ct).ConfigureAwait(false);

        if (!decision.IsAllowed)
        {
            var reason = decision.DeniedReasons.Count > 0
                ? string.Join("; ", decision.DeniedReasons)
                : $"Access to resource '{resourceName}' denied for subject '{principal}' in tenant '{tenantStr}'.";

            _logger.LogWarning("Envoy ext_authz DENIED: Subject '{Principal}', Tenant '{Tenant}', Resource '{Resource}', Method '{Method}' - Reason: {Reason}",
                principal, tenantStr, resourceName, method, reason);

            return EnvoyCheckResponse.Deny(403, reason);
        }

        _logger.LogInformation("Envoy ext_authz ALLOWED: Subject '{Principal}', Tenant '{Tenant}', Resource '{Resource}'",
            principal, tenantStr, resourceName);

        return EnvoyCheckResponse.Allow(principal, tenantStr, decision.CombinedRowFilterSql);
    }

    private static string? NormalizePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return "/";
        }

        // Strip query string and fragment
        var qIdx = rawPath.IndexOf('?');
        var pathOnly = qIdx >= 0 ? rawPath[..qIdx] : rawPath;
        var fIdx = pathOnly.IndexOf('#');
        pathOnly = fIdx >= 0 ? pathOnly[..fIdx] : pathOnly;

        if (pathOnly.Contains('\0'))
        {
            return null;
        }

        try
        {
            // Normalize via URI representation to resolve /../ and /./ segments
            var dummyUri = new Uri("http://localhost" + (pathOnly.StartsWith('/') ? pathOnly : "/" + pathOnly));
            return dummyUri.AbsolutePath;
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractResourceFromPath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            return "root";
        }

        // Skip common API prefix segments (e.g. /api/v1/customers/123 -> "customers")
        int idx = 0;
        while (idx < segments.Length &&
               (segments[idx].Equals("api", StringComparison.OrdinalIgnoreCase) ||
                segments[idx].StartsWith("v", StringComparison.OrdinalIgnoreCase) && segments[idx].Length <= 3))
        {
            idx++;
        }

        if (idx < segments.Length)
        {
            return segments[idx].ToLowerInvariant();
        }

        return segments[^1].ToLowerInvariant();
    }

    private static string? ExtractPrincipalFromAuthorizationHeader(string authHeader, out string? tenantId)
    {
        tenantId = null;
        if (string.IsNullOrWhiteSpace(authHeader))
        {
            return null;
        }

        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authHeader["Bearer ".Length..].Trim();
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return "bearer-token";
        }

        try
        {
            // Parse unencrypted JWT payload for subject & tenant claims
            var payloadBase64 = parts[1];
            payloadBase64 = payloadBase64.PadRight(payloadBase64.Length + (4 - payloadBase64.Length % 4) % 4, '=')
                                         .Replace('-', '+')
                                         .Replace('_', '/');

            var jsonBytes = Convert.FromBase64String(payloadBase64);
            using var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;

            if (root.TryGetProperty("tid", out var tidProp) || root.TryGetProperty("tenant_id", out tidProp))
            {
                tenantId = tidProp.GetString();
            }

            if (root.TryGetProperty("sub", out var subProp))
            {
                return subProp.GetString();
            }

            if (root.TryGetProperty("name", out var nameProp))
            {
                return nameProp.GetString();
            }
        }
        catch
        {
            // Ignore parsing errors and fallback gracefully
        }

        return "bearer-token";
    }

    public string GenerateIstioEnvoyFilterYaml(EnvoyFilterExportOptions? options = null)
    {
        var opts = options ?? new EnvoyFilterExportOptions();
        var sb = new StringBuilder();

        sb.AppendLine("apiVersion: networking.istio.io/v1alpha3");
        sb.AppendLine("kind: EnvoyFilter");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {opts.FilterName}");
        sb.AppendLine($"  namespace: {opts.MeshNamespace}");
        sb.AppendLine("spec:");
        sb.AppendLine("  workloadSelector:");
        sb.AppendLine("    labels:");
        sb.AppendLine("      istio: ingressgateway");
        sb.AppendLine("  configPatches:");
        sb.AppendLine("    - applyTo: HTTP_FILTER");
        sb.AppendLine("      match:");
        sb.AppendLine("        context: GATEWAY");
        sb.AppendLine("        listener:");
        sb.AppendLine("          filterChain:");
        sb.AppendLine("            filter:");
        sb.AppendLine("              name: envoy.filters.network.http_connection_manager");
        sb.AppendLine("              subFilter:");
        sb.AppendLine("                name: envoy.filters.http.router");
        sb.AppendLine("      patch:");
        sb.AppendLine("        operation: INSERT_BEFORE");
        sb.AppendLine("        value:");
        sb.AppendLine("          name: envoy.filters.http.ext_authz");
        sb.AppendLine("          typed_config:");
        sb.AppendLine("            \"@type\": type.googleapis.com/envoy.extensions.filters.http.ext_authz.v3.ExtAuthz");
        sb.AppendLine($"            failure_mode_allow: {(opts.FailOpen ? "true" : "false")}");
        sb.AppendLine("            http_service:");
        sb.AppendLine("              server_uri:");
        sb.AppendLine($"                uri: http://{opts.ServiceHost}:{opts.ServicePort}{opts.AuthzPath}");
        sb.AppendLine($"                cluster: outbound|{opts.ServicePort}||{opts.ServiceHost}");
        sb.AppendLine($"                timeout: {opts.TimeoutMs}ms");
        sb.AppendLine("              authorization_request:");
        sb.AppendLine("                allowed_headers:");
        sb.AppendLine("                  patterns:");
        sb.AppendLine("                    - exact: authorization");
        sb.AppendLine("                    - exact: x-tenant-id");
        sb.AppendLine("                    - exact: x-autheris-principal");
        sb.AppendLine("                    - exact: x-roles");
        sb.AppendLine("                    - prefix: x-feature-");
        sb.AppendLine("              authorization_response:");
        sb.AppendLine("                allowed_upstream_headers:");
        sb.AppendLine("                  patterns:");
        sb.AppendLine("                    - prefix: x-autheris-");
        sb.AppendLine("                    - exact: x-subgraph-variant");

        return sb.ToString();
    }

    public string GenerateIstioWasmPluginYaml(EnvoyFilterExportOptions? options = null)
    {
        var opts = options ?? new EnvoyFilterExportOptions();
        var sb = new StringBuilder();

        sb.AppendLine("apiVersion: extensions.istio.io/v1alpha1");
        sb.AppendLine("kind: WasmPlugin");
        sb.AppendLine("metadata:");
        sb.AppendLine($"  name: {opts.FilterName}-wasm");
        sb.AppendLine($"  namespace: {opts.MeshNamespace}");
        sb.AppendLine("spec:");
        sb.AppendLine("  selector:");
        sb.AppendLine("    matchLabels:");
        sb.AppendLine("      app: autheris-mesh-proxy");
        sb.AppendLine("  url: oci://ghcr.io/autheris/envoy-pdp-wasm:latest");
        sb.AppendLine("  phase: AUTHN");
        sb.AppendLine("  pluginConfig:");
        sb.AppendLine($"    endpoint: \"http://{opts.ServiceHost}:{opts.ServicePort}{opts.AuthzPath}\"");
        sb.AppendLine($"    timeoutMs: {opts.TimeoutMs}");
        sb.AppendLine($"    failClosed: {(!opts.FailOpen).ToString().ToLowerInvariant()}");
        sb.AppendLine("    cacheTtlSeconds: 15");

        return sb.ToString();
    }
}
