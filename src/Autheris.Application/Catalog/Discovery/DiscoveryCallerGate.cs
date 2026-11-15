namespace Autheris.Application.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Options;
using Autheris.Domain.Security;

/// <summary>
/// Enforces caller authentication, non-anonymous SID, resolved tenant, optional role/scope restrictions
/// (with per-tenant overrides), and per-(tenant, sid) rate limiting before any discovery repository read executes.
/// </summary>
public static class DiscoveryCallerGate
{
    public const string AnonymousMcpClientSid = "ANONYMOUS_MCP_CLIENT";
    public const string AnonymousSidValue = "S-1-5-21-ANONYMOUS";

    public static async ValueTask<Sid> EnsureAllowedAsync(
        CatalogCaller caller,
        CatalogDiscoveryOptions discoveryOptions,
        GatewayOptions gatewayOptions,
        IRateLimiterService? rateLimiter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(discoveryOptions);
        ArgumentNullException.ThrowIfNull(gatewayOptions);

        var principal = caller.Principal;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new DiscoveryStatusException(
                DiscoveryFailureKind.Unauthenticated,
                "UNAUTHORIZED",
                "Authentication is required for catalog discovery.");
        }

        var userSid = principal.GetUserSid();
        if (userSid is null ||
            string.IsNullOrWhiteSpace(userSid.Value.Value) ||
            string.Equals(userSid.Value.Value, AnonymousMcpClientSid, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(userSid.Value.Value, AnonymousSidValue, StringComparison.OrdinalIgnoreCase))
        {
            throw new DiscoveryStatusException(
                DiscoveryFailureKind.Unauthenticated,
                "UNAUTHORIZED",
                "A non-anonymous authenticated subject SID is required for catalog discovery.");
        }

        if (string.IsNullOrWhiteSpace(caller.Tenant.Value))
        {
            throw new DiscoveryStatusException(
                DiscoveryFailureKind.Unauthenticated,
                "UNAUTHORIZED",
                "A resolved tenant is required for catalog discovery.");
        }

        var settings = discoveryOptions.Discovery ?? new CatalogDiscoverySettings();
        var (requiredRoles, requiredScopes) = ResolveRequirements(settings, caller.Tenant);

        if (requiredRoles.Count > 0 && !HasAnyRole(principal, requiredRoles))
        {
            GatewayDiagnostics.ForbiddenRequestsCounter.Add(1);
            throw new DiscoveryStatusException(
                DiscoveryFailureKind.Forbidden,
                "DISCOVERY_NOT_PERMITTED",
                "Caller does not hold a required role for catalog discovery.");
        }

        if (requiredScopes.Count > 0 && !HasAnyScope(principal, requiredScopes))
        {
            GatewayDiagnostics.ForbiddenRequestsCounter.Add(1);
            throw new DiscoveryStatusException(
                DiscoveryFailureKind.Forbidden,
                "DISCOVERY_NOT_PERMITTED",
                "Caller token does not carry a required scope for catalog discovery.");
        }

        if (rateLimiter != null && !gatewayOptions.Insecure.warn_disable_rate_limiting)
        {
            var rpm = Math.Max(1, settings.RequestsPerMinute);
            var key = $"discovery:{caller.Tenant.Value}:{userSid.Value.Value}";
            var policy = new PostAuthSidRateLimitOptions
            {
                TokenBucketCapacity = rpm,
                TokensPerSecond = Math.Max(1, rpm / 60)
            };

            var rateResult = await rateLimiter.CheckPostAuthSidAsync(key, policy, ct).ConfigureAwait(false);
            if (!rateResult.Allowed)
            {
                throw new DiscoveryStatusException(
                    DiscoveryFailureKind.RateLimited,
                    "RATE_LIMIT_EXCEEDED",
                    "Discovery rate limit exceeded.",
                    Math.Max(1, rateResult.RetryAfterSeconds));
            }
        }

        return userSid.Value;
    }

    private static (IReadOnlyList<string> Roles, IReadOnlyList<string> Scopes) ResolveRequirements(
        CatalogDiscoverySettings settings,
        TenantId tenant)
    {
        if (settings.TenantOverrides != null &&
            settings.TenantOverrides.TryGetValue(tenant.Value, out var tenantOverride) &&
            tenantOverride != null)
        {
            return (
                tenantOverride.RequiredRoles ?? settings.RequiredRoles ?? [],
                tenantOverride.RequiredScopes ?? settings.RequiredScopes ?? []);
        }

        return (settings.RequiredRoles ?? [], settings.RequiredScopes ?? []);
    }

    private static bool HasAnyRole(ClaimsPrincipal principal, IReadOnlyList<string> requiredRoles)
    {
        var callerRoles = principal.GetUserRoles();
        foreach (var req in requiredRoles)
        {
            if (string.IsNullOrWhiteSpace(req))
            {
                continue;
            }

            if (callerRoles.Contains(req, StringComparer.OrdinalIgnoreCase) || principal.IsInRole(req))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAnyScope(ClaimsPrincipal principal, IReadOnlyList<string> requiredScopes)
    {
        var tokenScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in principal.Claims)
        {
            if (!string.Equals(claim.Type, "scope", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(claim.Type, "scp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                tokenScopes.Add(part);
            }
        }

        foreach (var req in requiredScopes)
        {
            if (!string.IsNullOrWhiteSpace(req) && tokenScopes.Contains(req))
            {
                return true;
            }
        }

        return false;
    }
}
