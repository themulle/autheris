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

    public async ValueTask<EnvoyCheckResponse> CheckAsync(
        EnvoyCheckRequest request,
        System.Security.Claims.ClaimsPrincipal? caller = null,
        CancellationToken ct = default)
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
                caller,
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
        System.Security.Claims.ClaimsPrincipal? caller = null,
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
                caller,
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
        System.Security.Claims.ClaimsPrincipal? caller,
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

        // 2. Extract Tenant (SEC C-1: Strictly from validated ClaimsPrincipal or trusted static context extensions)
        string tenantStr = "default";
        if (caller?.Identity?.IsAuthenticated == true)
        {
            var tClaim = caller.FindFirst("tenant_id")?.Value
                      ?? caller.FindFirst("tenant")?.Value
                      ?? caller.FindFirst("tid")?.Value;
            if (!string.IsNullOrWhiteSpace(tClaim))
            {
                tenantStr = tClaim;
            }
        }
        else if (contextExtensions.TryGetValue("tenant", out var ctxTenant) && !string.IsNullOrWhiteSpace(ctxTenant))
        {
            tenantStr = ctxTenant;
        }

        // 3. Extract Principal (SEC C-1: Strictly from validated ClaimsPrincipal or verified mTLS sourcePrincipal)
        string? principal = null;
        if (caller?.Identity?.IsAuthenticated == true)
        {
            principal = caller.FindFirst(System.Security.Claims.ClaimTypes.PrimarySid)?.Value
                     ?? caller.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                     ?? caller.FindFirst("sub")?.Value
                     ?? caller.Identity?.Name;
        }
        else if (!string.IsNullOrWhiteSpace(sourcePrincipal))
        {
            principal = sourcePrincipal;
        }
        else if (contextExtensions.TryGetValue("user", out var ctxUser) && !string.IsNullOrWhiteSpace(ctxUser))
        {
            principal = ctxUser;
        }

        if (string.IsNullOrWhiteSpace(principal))
        {
            principal = "anonymous";
        }

        // 4. Extract Groups / Roles (SEC C-1: Strictly from validated ClaimsPrincipal)
        var groupList = new List<Sid>();
        if (caller?.Identity?.IsAuthenticated == true)
        {
            foreach (var claim in caller.FindAll(c => c.Type is System.Security.Claims.ClaimTypes.Role
                                                             or System.Security.Claims.ClaimTypes.GroupSid
                                                             or "groups"
                                                             or "roles"))
            {
                if (!string.IsNullOrWhiteSpace(claim.Value))
                {
                    groupList.Add(new Sid(claim.Value));
                }
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

        // Parse Client IP (SEC H-4: Fail-closed to IPAddress.None instead of Loopback)
        IPAddress clientIp = IPAddress.None;
        if (!string.IsNullOrWhiteSpace(clientIpStr) && IPAddress.TryParse(clientIpStr, out var parsedIp))
        {
            clientIp = parsedIp;
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

        // SEC C-1: Do not leak raw CombinedRowFilterSql in header response
        return EnvoyCheckResponse.Allow(principal, tenantStr);
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

    public string GenerateIstioEnvoyFilterYaml(EnvoyFilterExportOptions? options = null)
    {
        var opts = options ?? new EnvoyFilterExportOptions();
        ValidateExportOptions(opts);
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
        // Review C-1 (rest): the ingress gateway must never forward client-supplied x-autheris-* headers. This Lua filter is
        // inserted FIRST (INSERT_BEFORE router, listed before the ext_authz patch) so that only values returned by the
        // ext_authz service (allowed_upstream_headers) can reach the upstream.
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
        sb.AppendLine("          name: envoy.filters.http.lua");
        sb.AppendLine("          typed_config:");
        sb.AppendLine("            \"@type\": type.googleapis.com/envoy.extensions.filters.http.lua.v3.Lua");
        sb.AppendLine("            default_source_code:");
        sb.AppendLine("              inline_string: |");
        sb.AppendLine("                function envoy_on_request(request_handle)");
        sb.AppendLine("                  local headers = request_handle:headers()");
        sb.AppendLine("                  local doomed = {}");
        sb.AppendLine("                  for key, _ in pairs(headers) do");
        sb.AppendLine("                    if string.sub(string.lower(key), 1, 11) == \"x-autheris-\" then");
        sb.AppendLine("                      doomed[#doomed + 1] = key");
        sb.AppendLine("                    end");
        sb.AppendLine("                  end");
        sb.AppendLine("                  for _, key in ipairs(doomed) do");
        sb.AppendLine("                    headers:remove(key)");
        sb.AppendLine("                  end");
        sb.AppendLine("                end");
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
        // G3: Envoy appends the original request path to http_service.path_prefix; without it the check would be sent to
        // the original path (e.g. /api/orders) instead of the PDP route.
        sb.AppendLine($"              path_prefix: {opts.AuthzPath}");
        sb.AppendLine("              server_uri:");
        sb.AppendLine($"                uri: http://{opts.ServiceHost}:{opts.ServicePort}{opts.AuthzPath}");
        sb.AppendLine($"                cluster: outbound|{opts.ServicePort}||{opts.ServiceHost}");
        sb.AppendLine($"                timeout: {opts.TimeoutMs}ms");
        sb.AppendLine("              authorization_request:");
        sb.AppendLine("                allowed_headers:");
        sb.AppendLine("                  patterns:");
        sb.AppendLine("                    - exact: authorization");
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
        ValidateExportOptions(opts);
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

    private static readonly Regex DnsLabelRegex = new("^[a-z0-9]([-a-z0-9]{0,61}[a-z0-9])?$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex AuthzPathRegex = new("^/[A-Za-z0-9/_.-]{0,200}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// G3: Export parameters are written into YAML unescaped, so they are restricted to DNS labels / numeric values.
    /// </summary>
    internal static void ValidateExportOptions(EnvoyFilterExportOptions opts)
    {
        if (opts.MeshNamespace is null || !DnsLabelRegex.IsMatch(opts.MeshNamespace))
        {
            throw new ArgumentException("Invalid namespace: must be a DNS label.", nameof(opts));
        }

        if (opts.FilterName is null || !DnsLabelRegex.IsMatch(opts.FilterName))
        {
            throw new ArgumentException("Invalid filter name: must be a DNS label.", nameof(opts));
        }

        if (opts.ServiceHost is null || opts.ServiceHost.Length > 253 || !opts.ServiceHost.Split('.').All(l => DnsLabelRegex.IsMatch(l)))
        {
            throw new ArgumentException("Invalid host: must be a DNS name.", nameof(opts));
        }

        if (opts.ServicePort is < 1 or > 65535)
        {
            throw new ArgumentException("Invalid port.", nameof(opts));
        }

        if (opts.AuthzPath is null || !AuthzPathRegex.IsMatch(opts.AuthzPath))
        {
            throw new ArgumentException("Invalid authorization path.", nameof(opts));
        }

        if (opts.TimeoutMs is < 1 or > 60000)
        {
            throw new ArgumentException("Invalid timeout.", nameof(opts));
        }
    }
}
