namespace Autheris.Api.Security;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Autheris.Application.Security;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Architecture Phase 1: Canonical factory that transforms an authenticated HttpContext
/// into an immutable, strictly validated SecurityPrincipalContext.
/// </summary>
public static class SecurityContextFactory
{
    public const string TenantHeaderName = "X-Tenant-ID";
    public const string JustificationHeaderName = "X-Break-Glass-Justification";
    public const string JustificationHeaderNameAlt = "X-Justification";
    public const string CallerAppHeaderName = "X-Caller-Application";
    public const string CallerAppHeaderNameAlt = "X-Application-Name";
    public const string CallerVersionHeaderName = "X-Caller-Version";
    public const string CallerVersionHeaderNameAlt = "X-Application-Version";
    public const string CallerQueryHeaderName = "X-Caller-Query-Name";
    public const string CallerQueryHeaderNameAlt = "X-Query-Name";

    public static SecurityPrincipalContext CreateFromHttpContext(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var caller = ExtractCallerContext(context);
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return SecurityPrincipalContext.Anonymous(caller: caller);
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

        // 4. Resolve the tenant (DT-A-2..4): claims only. The claims transformation stamped the resolved tenant (claim, realm binding or the
        // configured default tenant), so a claim-less principal is unresolved here, never a legacy sentinel. An invalid claim is flagged
        // (the middleware gate answers 403 TENANT_CLAIM_INVALID) and the header is not even looked at.
        var claim = TenantClaimReader.Read(user);
        if (claim.Kind == ClaimTenantKind.Invalid)
        {
            return BuildContext(context, caller, userSid, groupSids, tenantRoles, clusterRoles, null, true, null, false, null);
        }

        TenantId? claimTenant = claim.Kind == ClaimTenantKind.Valid ? (TenantId?)claim.Tenant : null;
        var claimSource = user.FindFirst(TenantSourceClaim.ClaimType)?.Value;

        // DT-SC-09: X-Tenant-ID (one spelling, header names are case-insensitive) selects a tenant for a canonical ClusterAdmin only; it is
        // never a fallback when resolution fails.
        string? headerTenant = null;
        if (context.Request.Headers.TryGetValue(TenantHeaderName, out var hv) && !string.IsNullOrWhiteSpace(hv))
        {
            headerTenant = hv.ToString().Trim();
        }

        TenantId? finalTenant = claimTenant;
        string? source = claimTenant is null ? null : claimSource;
        var tenantSelectedByHeader = false;
        if (!string.IsNullOrWhiteSpace(headerTenant) &&
            (claimTenant is null || !string.Equals(claimTenant.Value.Value, headerTenant, StringComparison.OrdinalIgnoreCase)))
        {
            if (!isClusterAdmin)
            {
                throw new SecurityException(claimTenant is null
                    ? "Cross-tenant access forbidden. Authenticated user lacks tenant claim and is not authorized to select arbitrary tenant."
                    : "Cross-tenant access forbidden. Header tenant does not match authenticated token claim.");
            }

            finalTenant = new TenantId(headerTenant);
            tenantSelectedByHeader = true;
            source = TenantSourceClaim.Header;
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

        return BuildContext(context, caller, userSid, groupSids, tenantRoles, clusterRoles, finalTenant, false, source, tenantSelectedByHeader, justification);
    }

    private static SecurityPrincipalContext BuildContext(
        HttpContext context,
        CallerContext? caller,
        Sid userSid,
        IReadOnlySet<Sid> groupSids,
        IReadOnlySet<string> tenantRoles,
        IReadOnlySet<string> clusterRoles,
        TenantId? tenant,
        bool claimInvalid,
        string? source,
        bool selectedByHeader,
        string? justification)
    {
        var user = context.User;
        // 6. Extract Client IP
        var clientIp = context.RequestServices?.GetService(typeof(Autheris.Application.Interfaces.IClientIpResolver)) is Autheris.Application.Interfaces.IClientIpResolver ipResolver
            ? ipResolver.ResolveClientIp()
            : context.Connection.RemoteIpAddress;

        return new SecurityPrincipalContext
        {
            UserSid = userSid,
            ResolvedTenant = tenant,
            TenantClaimInvalid = claimInvalid,
            TenantSource = source,
            GroupSids = groupSids,
            TenantRoles = tenantRoles,
            ClusterRoles = clusterRoles,
            AuthenticationScheme = user.Identity?.AuthenticationType ?? "Unknown",
            IsAuthenticated = true,
            TenantSelectedByHeader = selectedByHeader,
            BreakGlassJustification = justification,
            ClientIp = clientIp,
            Caller = caller
        };
    }

    private static CallerContext? ExtractCallerContext(HttpContext context)
    {
        string? app = GetRawHeader(context, CallerAppHeaderName, CallerAppHeaderNameAlt);
        string? ver = GetRawHeader(context, CallerVersionHeaderName, CallerVersionHeaderNameAlt);
        string? qry = GetRawHeader(context, CallerQueryHeaderName, CallerQueryHeaderNameAlt);
        string? clientId = context.User.FindFirst("azp")?.Value
            ?? context.User.FindFirst("appid")?.Value
            ?? context.User.FindFirst("client_id")?.Value;

        if (app == null && ver == null && qry == null && clientId == null) return null;

        var normalizedApp = CallerLabelNormalizer.NormalizeApplication(app);
        var normalizedVer = CallerLabelNormalizer.NormalizeVersion(ver);
        var normalizedQry = CallerLabelNormalizer.NormalizeQueryName(qry);
        var normalizedClientId = CallerLabelNormalizer.NormalizeClientId(clientId);

        return new CallerContext(normalizedApp, normalizedVer, normalizedQry, normalizedClientId);
    }

    private static string? GetRawHeader(HttpContext context, string primary, string alt)
    {
        if (context.Request.Headers.TryGetValue(primary, out var val) && !string.IsNullOrWhiteSpace(val))
        {
            return val.ToString();
        }
        if (context.Request.Headers.TryGetValue(alt, out var altVal) && !string.IsNullOrWhiteSpace(altVal))
        {
            return altVal.ToString();
        }
        return null;
    }

    private static string? GetHeader(HttpContext context, string primary, string alt)
    {
        var raw = GetRawHeader(context, primary, alt);
        return raw != null ? SanitizeCallerHeader(raw) : null;
    }

    private static string? SanitizeCallerHeader(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var norm = CallerLabelNormalizer.NormalizeApplication(value);
        return norm is CallerLabelNormalizer.InvalidSentinel ? null : norm;
    }
}
