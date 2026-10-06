namespace Autheris.Api.Security;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Architecture Phase 1: Canonical factory that transforms an authenticated HttpContext
/// into an immutable, strictly validated SecurityPrincipalContext.
/// </summary>
public static class SecurityContextFactory
{
    public const string TenantHeaderName = "X-Tenant-ID";
    public const string TenantHeaderNameAlt = "X-Tenant-Id";
    public const string JustificationHeaderName = "X-Break-Glass-Justification";
    public const string JustificationHeaderNameAlt = "X-Justification";

    public static SecurityPrincipalContext CreateFromHttpContext(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return SecurityPrincipalContext.Anonymous();
        }

        // 1. Resolve canonical SID (RV-02: never fall back to the display name – fail closed)
        var userSid = user.GetUserSid()
            ?? throw new UnauthorizedAccessException("Authenticated principal lacks a valid SID claim (PrimarySid, objectSid or NameIdentifier).");

        // 2. Resolve Groups
        var groupSids = user.GetGroupSids();

        // 3. Resolve Roles and partition into Cluster vs Tenant
        // RV-03: cluster scope is decided exclusively by ClusterAdminPolicy (canonical aliases, no ForwardAuth).
        var rawRoles = user.GetUserRoles();
        var clusterRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tenantRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool clusterAdminAllowed = ClusterAdminPolicy.IsCanonicalClusterAdmin(user);

        foreach (var r in rawRoles)
        {
            if (ClusterAdminPolicy.IsClusterAdminRole(r))
            {
                if (clusterAdminAllowed)
                {
                    clusterRoles.Add("ClusterAdmin");
                }
            }
            else
            {
                tenantRoles.Add(r);
            }
        }

        bool isClusterAdmin = clusterRoles.Contains("ClusterAdmin");

        // 4. Resolve Tenant with strict cross-tenant protection
        // Review (Low): a malformed tenant claim must fail the request (SecurityException -> 403); it must never
        // silently degrade to the legacy tenant.
        string? claimTenant = null;
        var resolvedClaim = user.GetTenantId();
        if (resolvedClaim != TenantId.LegacySingleTenant)
        {
            claimTenant = resolvedClaim.Value;
        }

        string? headerTenant = null;
        if (context.Request.Headers.TryGetValue(TenantHeaderName, out var hv) && !string.IsNullOrWhiteSpace(hv))
        {
            headerTenant = hv.ToString().Trim();
        }
        else if (context.Request.Headers.TryGetValue(TenantHeaderNameAlt, out var hva) && !string.IsNullOrWhiteSpace(hva))
        {
            headerTenant = hva.ToString().Trim();
        }

        TenantId finalTenant;
        if (!string.IsNullOrWhiteSpace(claimTenant))
        {
            if (!string.IsNullOrWhiteSpace(headerTenant) &&
                !string.Equals(claimTenant, headerTenant, StringComparison.OrdinalIgnoreCase))
            {
                if (!isClusterAdmin)
                {
                    throw new SecurityException("Cross-tenant access forbidden. Header tenant does not match authenticated token claim.");
                }
                finalTenant = new TenantId(headerTenant);
            }
            else
            {
                finalTenant = new TenantId(claimTenant);
            }
        }
        else
        {
            // Authenticated without a tenant claim
            if (!string.IsNullOrWhiteSpace(headerTenant))
            {
                if (!isClusterAdmin)
                {
                    throw new SecurityException("Cross-tenant access forbidden. Authenticated user lacks tenant claim and is not authorized to select arbitrary tenant.");
                }
                finalTenant = new TenantId(headerTenant);
            }
            else
            {
                finalTenant = TenantId.LegacySingleTenant;
            }
        }

        // 5. Extract Break-Glass Justification
        string? justification = null;
        if (context.Request.Headers.TryGetValue(JustificationHeaderName, out var jv) && !string.IsNullOrWhiteSpace(jv))
        {
            justification = jv.ToString().Trim();
        }
        else if (context.Request.Headers.TryGetValue(JustificationHeaderNameAlt, out var jva) && !string.IsNullOrWhiteSpace(jva))
        {
            justification = jva.ToString().Trim();
        }

        // 6. Extract Client IP
        var clientIp = context.RequestServices?.GetService(typeof(Autheris.Application.Interfaces.IClientIpResolver)) is Autheris.Application.Interfaces.IClientIpResolver ipResolver
            ? ipResolver.ResolveClientIp()
            : context.Connection.RemoteIpAddress;

        return new SecurityPrincipalContext
        {
            UserSid = userSid,
            TenantId = finalTenant,
            GroupSids = groupSids,
            TenantRoles = tenantRoles,
            ClusterRoles = clusterRoles,
            AuthenticationScheme = user.Identity?.AuthenticationType ?? "Unknown",
            IsAuthenticated = true,
            BreakGlassJustification = justification,
            ClientIp = clientIp
        };
    }
}
