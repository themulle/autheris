namespace Autheris.Api.Middleware;

using Autheris.Api.Errors;
using Autheris.Api.Security;
using System;
using System.Diagnostics;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Runtime.ResourceGroups;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class ResourceGroupMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IResourceGroupManager _resourceGroupManager;
    private readonly GatewayOptions _options;
    private readonly ILogger<ResourceGroupMiddleware> _logger;
    private readonly PersistentConnectionLimiter _connectionLimiter;

    public ResourceGroupMiddleware(
        RequestDelegate next,
        IResourceGroupManager resourceGroupManager,
        IOptions<GatewayOptions> options,
        ILogger<ResourceGroupMiddleware> logger,
        PersistentConnectionLimiter? connectionLimiter = null)
    {
        _next = next;
        _resourceGroupManager = resourceGroupManager;
        _options = options.Value;
        _logger = logger;
        _connectionLimiter = connectionLimiter ?? new PersistentConnectionLimiter(_options.ResourceGroups);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        // Skip system, metrics, and health endpoints so observability is never starved
        if (GatewayEndpointMetadata.IsHealthProbe(context) ||
            GatewayEndpointMetadata.IsMetricsEndpoint(context) ||
            path.StartsWithSegments("/api/governance/system", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Resolve Tenant and sanitize against CRLF log injection
        var tenantId = ResolveTenantKey(context);

        // Long-lived connections (WebSocket upgrade, SSE) must not hold resource group slots.
        // They are limited per principal (SID) and per tenant instead.
        if (IsPersistentConnectionRequest(context, _options))
        {
            var principalKey = ResolvePrincipalKey(context);
            var connectionLease = _connectionLimiter.TryAcquire(principalKey, tenantId);
            if (connectionLease == null)
            {
                _logger.LogWarning(
                    "Persistent connection limit reached for tenant {TenantId} (max {MaxPerPrincipal} per principal / {MaxPerTenant} per tenant).",
                    tenantId, _connectionLimiter.MaxPerPrincipal, _connectionLimiter.MaxPerTenant);
                await ApiProblems.WriteTooManyRequests(context, ApiErrorCodes.PersistentConnectionLimitExceeded, "Too many concurrent long-lived connections (WebSocket/SSE) for this principal or tenant.", 30, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            using (connectionLease)
            {
                await _next(context);
            }
            return;
        }

        // Classify workload tier
        var tier = ClassifyWorkloadTier(context, _options);
        RecordResolvedTierTelemetry(context, tier);

        var leaseResult = await _resourceGroupManager.TryAcquireLeaseAsync(tier, tenantId, context.RequestAborted).ConfigureAwait(false);

        if (!leaseResult.Success)
        {
            context.Response.Headers["X-Resource-Group-Tier"] = tier.ToString();

            if (string.Equals(leaseResult.RejectionReason, "QueueFull", StringComparison.OrdinalIgnoreCase))
            {
                await ApiProblems.WriteTooManyRequests(context, ApiErrorCodes.ResourceGroupQueueFull, "The resource group queue is full. Please retry later.", 5, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            // Timed out waiting in queue
            await ApiProblems.WriteAsync(context, StatusCodes.Status504GatewayTimeout, ApiErrorCodes.ResourceGroupTimeout, "The resource group lease acquisition timed out.", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        context.Response.Headers["X-Resource-Group-Tier"] = tier.ToString();

        if (leaseResult.Lease != null)
        {
            await using (leaseResult.Lease.ConfigureAwait(false))
            {
                await _next(context);
            }
        }
        else
        {
            await _next(context);
        }
    }

    /// <summary>
    /// SEC H-07: Detects requests that open a long-lived connection: WebSocket upgrades (HTTP/1.1 Upgrade or
    /// HTTP/2 extended CONNECT), Server-Sent Events (Accept: text/event-stream) and the MCP SSE handshake.
    /// Detection is header based because UseWebSockets runs later in the pipeline.
    /// </summary>
    internal static bool IsPersistentConnectionRequest(HttpContext context, GatewayOptions? options = null)
    {
        if (context.Features.Get<IHttpUpgradeFeature>()?.IsUpgradableRequest == true ||
            context.Features.Get<IHttpExtendedConnectFeature>()?.IsExtendedConnect == true)
        {
            return true;
        }

        var upgradeHeader = context.Request.Headers.Upgrade.ToString();
        if (upgradeHeader.Contains("websocket", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool isMcpRequest = GatewayEndpointMetadata.HasTransport(context, DataSourceTransportFlags.Mcp) ||
                            McpPathResolver.IsMcpRequest(context.Request.Path, options);

        // MCP streamable-HTTP POSTs advertise text/event-stream but are short-lived tool calls: keep them in the slots.
        var accept = context.Request.Headers.Accept.ToString();
        if (accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) &&
            (HttpMethods.IsGet(context.Request.Method) || !isMcpRequest))
        {
            return true;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        return isMcpRequest &&
               path.EndsWith("/sse", StringComparison.OrdinalIgnoreCase);
    }

    internal const string UnscopedTenantKey = "_unscoped";

    internal const string InvalidTenantKey = "_invalid";

    internal static string ResolveTenantKey(HttpContext context)
    {
        string? rawTenantId = null;
        if (context.Items.TryGetValue(SecurityPrincipalContext.TenantIdItemKey, out var tObj))
        {
            rawTenantId = tObj switch
            {
                TenantId tid => tid.Value,
                string tStr => tStr,
                _ => null
            };
        }

        if (string.IsNullOrWhiteSpace(rawTenantId))
        {
            // DT-SC-03: the standard reader; callers without a valid tenant share reserved buckets ('_' is not a valid tenant prefix), never "default".
            var claim = Autheris.Domain.Tenancy.TenantClaimReader.Read(context.User);
            return claim.Kind switch
            {
                Autheris.Domain.Tenancy.ClaimTenantKind.Valid => claim.Tenant.Value!,
                Autheris.Domain.Tenancy.ClaimTenantKind.Invalid => InvalidTenantKey,
                _ => UnscopedTenantKey
            };
        }

        var tenantId = rawTenantId.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal).Trim();
        if (tenantId.Length > 64) tenantId = tenantId[..64];
        return tenantId.Length == 0 ? UnscopedTenantKey : tenantId;
    }

    internal static string ResolvePrincipalKey(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true)
        {
            var sid = user.FindFirst(ClaimTypes.PrimarySid)?.Value
                ?? user.FindFirst("objectSid")?.Value
                ?? user.FindFirst("oid")?.Value
                ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? user.Identity.Name;
            if (!string.IsNullOrWhiteSpace(sid))
            {
                return "sid:" + sid;
            }
        }

        // Anonymous connections are bucketed by client IP (after trusted-proxy resolution).
        return "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    }

    /// <summary>
    /// Resolves the tier from the least privileged of the path tier (<c>/mcp</c> agents, <c>/odata</c> bulk, else interactive)
    /// and any mapped token scope tier (<c>Gateway:ResourceGroups:ScopeTiers</c>). A client-chosen <c>X-Workload-Tier</c> header
    /// is only honoured when it does not raise the caller above that resolved tier (anti-noisy-neighbor);
    /// ClusterAdmin / GovernanceAdmin may request any tier.
    /// </summary>
    internal static ResourceGroupTier ClassifyWorkloadTier(HttpContext context, GatewayOptions? options = null)
    {
        var pathTier = ResolvePathTier(context, options);
        var scopeTier = ResolveScopeTier(context.User, options);
        var resolved = scopeTier is { } s && PrivilegeRank(s) > PrivilegeRank(pathTier) ? s : pathTier;

        if (!context.Request.Headers.TryGetValue("X-Workload-Tier", out var headerVal) ||
            ParseRequestedTier(headerVal.ToString()) is not { } requested)
        {
            return resolved;
        }

        if (PrivilegeRank(requested) >= PrivilegeRank(resolved))
        {
            return requested;
        }

        if (ClusterAdminPolicy.IsCanonicalClusterAdmin(context.User) ||
            Autheris.Api.Security.GatewayPolicies.HasRequestRole(context, GatewayRole.GovernanceAdmin))
        {
            return requested;
        }

        // A more privileged tier than the resolved one is ignored.
        return resolved;
    }

    private static void RecordResolvedTierTelemetry(HttpContext context, ResourceGroupTier tier)
    {
        var tierName = tier.ToString();
        context.Features.Get<AuditContext>()?.AdditionalDetails["resourceGroupTier"] = tierName;
        Activity.Current?.SetTag("autheris.resource_group.tier", tierName);
    }

    private static ResourceGroupTier? ResolveScopeTier(ClaimsPrincipal? principal, GatewayOptions? options)
    {
        var scopeTiers = options?.ResourceGroups?.ScopeTiers;
        if (scopeTiers is not { Count: > 0 } || principal is null)
        {
            return null;
        }

        var tokenScopes = TokenAccessScope.GetTokenScopes(principal);
        if (tokenScopes.Count == 0)
        {
            return null;
        }

        ResourceGroupTier? leastPrivileged = null;
        foreach (var rawScope in tokenScopes)
        {
            var bareScope = ScopeNames.ToBareName(rawScope.Trim());
            if (bareScope.Length == 0)
            {
                continue;
            }

            if (!TryGetMappedTier(scopeTiers, bareScope, out var mapped) || !Enum.IsDefined(mapped))
            {
                continue;
            }

            if (leastPrivileged is null || PrivilegeRank(mapped) > PrivilegeRank(leastPrivileged.Value))
            {
                leastPrivileged = mapped;
            }
        }

        return leastPrivileged;
    }

    private static bool TryGetMappedTier(
        IReadOnlyDictionary<string, ResourceGroupTier> scopeTiers,
        string bareScope,
        out ResourceGroupTier tier)
    {
        if (scopeTiers.TryGetValue(bareScope, out tier))
        {
            return true;
        }

        foreach (var (configuredScope, configuredTier) in scopeTiers)
        {
            if (string.Equals(ScopeNames.ToBareName(configuredScope.Trim()), bareScope, StringComparison.OrdinalIgnoreCase))
            {
                tier = configuredTier;
                return true;
            }
        }

        tier = default;
        return false;
    }

    private static ResourceGroupTier ResolvePathTier(HttpContext context, GatewayOptions? options)
    {
        if (GatewayEndpointMetadata.HasTransport(context, DataSourceTransportFlags.Mcp) ||
            McpPathResolver.IsMcpRequest(context.Request.Path, options))
        {
            return ResourceGroupTier.AutonomousAgents;
        }

        if (GatewayEndpointMetadata.HasTransport(context, DataSourceTransportFlags.OData) ||
            context.Request.Path.StartsWithSegments("/odata", StringComparison.OrdinalIgnoreCase))
        {
            return ResourceGroupTier.BulkAnalytics;
        }

        return ResourceGroupTier.Interactive;
    }

    internal static ResourceGroupTier? ParseRequestedTier(string? header)
    {
        var headerStr = header?.Trim() ?? string.Empty;
        if (headerStr.Equals("AutonomousAgent", StringComparison.OrdinalIgnoreCase) ||
            headerStr.Equals("AutonomousAgents", StringComparison.OrdinalIgnoreCase) ||
            headerStr.Equals("Agent", StringComparison.OrdinalIgnoreCase))
        {
            return ResourceGroupTier.AutonomousAgents;
        }

        if (headerStr.Equals("BulkAnalytics", StringComparison.OrdinalIgnoreCase) ||
            headerStr.Equals("Analytics", StringComparison.OrdinalIgnoreCase) ||
            headerStr.Equals("Bulk", StringComparison.OrdinalIgnoreCase))
        {
            return ResourceGroupTier.BulkAnalytics;
        }

        if (headerStr.Equals("Interactive", StringComparison.OrdinalIgnoreCase))
        {
            return ResourceGroupTier.Interactive;
        }

        return null;
    }

    /// <summary>
    /// Privilege order of the tiers by their default capacity (concurrency 50 / 10 / 5, shortest queue timeout first):
    /// Interactive (0) is the most privileged, then AutonomousAgents (1), then BulkAnalytics (2). A higher rank is a
    /// downgrade and may always be chosen by the client.
    /// </summary>
    internal static int PrivilegeRank(ResourceGroupTier tier) => tier switch
    {
        ResourceGroupTier.Interactive => 0,
        ResourceGroupTier.AutonomousAgents => 1,
        ResourceGroupTier.BulkAnalytics => 2,
        _ => int.MaxValue
    };
}
