namespace Autheris.Api.Middleware;

using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.ResourceGroups;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class ResourceGroupMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IResourceGroupManager _resourceGroupManager;
    private readonly ILogger<ResourceGroupMiddleware> _logger;
    private readonly bool _enabled;
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
        _logger = logger;
        _enabled = options.Value.ResourceGroups?.Enabled ?? true;
        _connectionLimiter = connectionLimiter ?? new PersistentConnectionLimiter(options.Value.ResourceGroups);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_enabled)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path;

        // Skip system, metrics, and health endpoints so observability is never starved
        if (path.StartsWithSegments("/health") ||
            path.StartsWithSegments("/metrics") ||
            path.StartsWithSegments("/api/governance/system"))
        {
            await _next(context);
            return;
        }

        // Resolve Tenant and sanitize against CRLF log injection
        var tenantId = ResolveTenantKey(context);

        // SEC H-07: Long-lived connections (WebSocket upgrade, SSE) must not hold resource group slots.
        // They are limited per principal (SID) and per tenant instead.
        if (IsPersistentConnectionRequest(context))
        {
            var principalKey = ResolvePrincipalKey(context);
            var connectionLease = _connectionLimiter.TryAcquire(principalKey, tenantId);
            if (connectionLease == null)
            {
                _logger.LogWarning(
                    "Persistent connection limit reached for tenant {TenantId} (max {MaxPerPrincipal} per principal / {MaxPerTenant} per tenant).",
                    tenantId, _connectionLimiter.MaxPerPrincipal, _connectionLimiter.MaxPerTenant);
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers["Retry-After"] = "30";
                await context.Response.WriteAsJsonAsync(new
                {
                    errors = new[]
                    {
                        new
                        {
                            message = "Too many concurrent long-lived connections (WebSocket/SSE) for this principal or tenant.",
                            extensions = new
                            {
                                code = "PERSISTENT_CONNECTION_LIMIT_EXCEEDED"
                            }
                        }
                    }
                }, cancellationToken: context.RequestAborted);
                return;
            }

            using (connectionLease)
            {
                await _next(context);
            }
            return;
        }

        // Classify workload tier
        var tier = ClassifyWorkloadTier(context);

        var leaseResult = await _resourceGroupManager.TryAcquireLeaseAsync(tier, tenantId, context.RequestAborted).ConfigureAwait(false);

        if (!leaseResult.Success)
        {
            context.Response.Headers["X-Resource-Group-Tier"] = tier.ToString();

            if (string.Equals(leaseResult.RejectionReason, "QueueFull", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers["Retry-After"] = "5";
                await context.Response.WriteAsJsonAsync(new
                {
                    errors = new[]
                    {
                        new
                        {
                            message = $"Resource group queue full for tier '{tier}'. Please retry later.",
                            extensions = new
                            {
                                code = "RESOURCE_GROUP_QUEUE_FULL",
                                tier = tier.ToString()
                            }
                        }
                    }
                }, cancellationToken: context.RequestAborted);
                return;
            }

            // Timed out waiting in queue
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            await context.Response.WriteAsJsonAsync(new
            {
                errors = new[]
                {
                    new
                    {
                        message = $"Resource group lease acquisition timed out for tier '{tier}'.",
                        extensions = new
                        {
                            code = "RESOURCE_GROUP_TIMEOUT",
                            tier = tier.ToString()
                        }
                    }
                }
            }, cancellationToken: context.RequestAborted);
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
    internal static bool IsPersistentConnectionRequest(HttpContext context)
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

        // MCP streamable-HTTP POSTs advertise text/event-stream but are short-lived tool calls: keep them in the slots.
        var accept = context.Request.Headers.Accept.ToString();
        if (accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) &&
            (HttpMethods.IsGet(context.Request.Method) || !context.Request.Path.StartsWithSegments("/mcp")))
        {
            return true;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        return context.Request.Path.StartsWithSegments("/mcp") &&
               path.EndsWith("/sse", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ResolveTenantKey(HttpContext context)
    {
        string? rawTenantId = null;
        if (context.Items.TryGetValue(TenantResolutionMiddleware.TenantIdItemKey, out var tObj))
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
            rawTenantId = context.User.FindFirst("tenant_id")?.Value
                ?? context.User.FindFirst("tid")?.Value
                ?? context.User.FindFirst("tenant")?.Value
                ?? "default";
        }

        var tenantId = rawTenantId.Replace("\r", string.Empty).Replace("\n", string.Empty).Trim();
        if (tenantId.Length > 64) tenantId = tenantId[..64];
        return tenantId.Length == 0 ? "default" : tenantId;
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

    private static ResourceGroupTier ClassifyWorkloadTier(HttpContext context)
    {
        var path = context.Request.Path;
        var isMcp = path.StartsWithSegments("/mcp");
        var isOData = path.StartsWithSegments("/odata");

        // 1. Explicit Client Header
        if (context.Request.Headers.TryGetValue("X-Workload-Tier", out var headerVal))
        {
            var headerStr = headerVal.ToString().Trim();
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
                // Anti-Noisy-Neighbor Security Guard (SEC-02):
                // Do not allow automated /mcp or /odata paths to promote themselves to Interactive
                // unless caller has elevated ClusterAdmin or GovernanceAdmin role!
                if (!isMcp && !isOData)
                {
                    return ResourceGroupTier.Interactive;
                }

                if (context.User.IsInRole("ClusterAdmin") || context.User.IsInRole("GovernanceAdmin"))
                {
                    return ResourceGroupTier.Interactive;
                }

                // Fall through to path-determined tier below
            }
        }

        // 2. Path-based classification
        if (isMcp)
        {
            return ResourceGroupTier.AutonomousAgents;
        }

        if (isOData)
        {
            return ResourceGroupTier.BulkAnalytics;
        }

        // 3. Default to Interactive
        return ResourceGroupTier.Interactive;
    }
}
