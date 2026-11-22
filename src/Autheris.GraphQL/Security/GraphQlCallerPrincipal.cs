namespace Autheris.GraphQL.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using HotChocolate.Collections.Immutable;
using HotChocolate.Execution;
using HotChocolate.Resolvers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Single caller-principal and context resolution helper for all GraphQL resolvers and execution middlewares.
/// Prefers the operation/session <see cref="ClaimsPrincipal"/> stored in HotChocolate context data
/// (e.g. MCP curated sessions) over ambient HTTP transport state.
/// </summary>
public static class GraphQlCallerPrincipal
{
    private static readonly GatewayRoleEvaluator RoleEvaluator = new();
    private static readonly FieldInfo? BuilderContextDataField =
        typeof(OperationRequestBuilder).GetField("_contextData", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? BuilderReadOnlyContextDataField =
        typeof(OperationRequestBuilder).GetField("_readOnlyContextData", BindingFlags.Instance | BindingFlags.NonPublic);

    private sealed class McpSessionStateMarker
    {
        public static readonly McpSessionStateMarker Instance = new();
    }

    public static ClaimsPrincipal BuildMcpSessionPrincipal(McpSessionContext sessionContext)
    {
        ArgumentNullException.ThrowIfNull(sessionContext);

        var effectiveUserId = !string.IsNullOrWhiteSpace(sessionContext.UserSid)
            ? sessionContext.UserSid
            : sessionContext.ServicePrincipalId;

        var sessionPrincipal = McpProtocolHandler.BuildPrincipalFromSession(sessionContext)!;
        var claims = new List<Claim>(sessionPrincipal.Claims);
        // Ensure sub reflects the resolved session subject rather than any carried token claim.
        claims.RemoveAll(c => c.Type == "sub");
        if (!string.IsNullOrWhiteSpace(effectiveUserId))
        {
            claims.Add(new Claim("sub", effectiveUserId));
        }

        if (!string.IsNullOrWhiteSpace(sessionContext.ServicePrincipalId) &&
            !string.Equals(sessionContext.ServicePrincipalId, effectiveUserId, StringComparison.Ordinal))
        {
            claims.RemoveAll(c => c.Type is "client_id" or "azp");
            claims.Add(new Claim("client_id", sessionContext.ServicePrincipalId));
            claims.Add(new Claim("azp", sessionContext.ServicePrincipalId));
        }

        var identity = new ClaimsIdentity(claims, CallerAttributeSchemeGate.McpAuthScheme);
        if (sessionContext.IsReadOnly)
        {
            TokenAccessScope.MarkReadOnly(identity);
        }

        return new ClaimsPrincipal(identity);
    }

    public static CallerSecurityContext ResolveCallerSecurityContext(
        ClaimsPrincipal principal,
        TenantId fallbackTenant = default,
        Sid? fallbackUserSid = null,
        bool allowGroups = true)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var userSid = principal.GetUserSid()
            ?? fallbackUserSid
            ?? throw new InvalidOperationException("Authenticated caller principal has no subject SID.");

        var isAppOnly = principal.Claims.Any(c =>
            string.Equals(c.Type, McpProtocolHandler.IdentityKindClaimType, StringComparison.Ordinal) &&
            string.Equals(c.Value, McpProtocolHandler.ApplicationIdentityKind, StringComparison.Ordinal));

        var groupSids = allowGroups && !isAppOnly
            ? principal.GetGroupSids().ToArray()
            : Array.Empty<Sid>();

        var roles = principal.GetUserRoles().ToArray();

        var claimTenant = TenantClaimReader.Read(principal);
        if (claimTenant.Kind == ClaimTenantKind.Invalid)
        {
            throw new TenantClaimInvalidException();
        }

        TenantId tenantId;
        if (fallbackTenant != default)
        {
            tenantId = fallbackTenant;
        }
        else if (claimTenant.Kind == ClaimTenantKind.Valid)
        {
            tenantId = claimTenant.Tenant;
        }
        else
        {
            throw new TenantUnresolvedException();
        }

        var isGovernanceAdmin = HasGatewayRole(principal, GatewayRole.GovernanceAdmin, tenantId.Value);
        var isClusterAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);

        return new CallerSecurityContext(
            userSid,
            groupSids,
            roles,
            tenantId,
            isGovernanceAdmin,
            isClusterAdmin);
    }

    public static void SetMcpSessionState(
        OperationRequestBuilder requestBuilder,
        ClaimsPrincipal principal,
        McpSessionContext sessionContext)
    {
        ArgumentNullException.ThrowIfNull(requestBuilder);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(sessionContext);

        if (requestBuilder.Features.Get<McpSessionStateMarker>() is not null)
        {
            throw new InvalidOperationException("MCP session principal and security context have already been configured for this GraphQL request.");
        }

        if (HasGlobalStateKey(requestBuilder, nameof(ClaimsPrincipal)))
        {
            throw new InvalidOperationException("GraphQL global state 'ClaimsPrincipal' was already configured.");
        }

        if (HasGlobalStateKey(requestBuilder, nameof(CallerSecurityContext)))
        {
            throw new InvalidOperationException("GraphQL global state 'CallerSecurityContext' was already configured.");
        }

        var sessionTenant = new TenantId(sessionContext.TenantId);
        var claimTenant = TenantClaimReader.Read(principal);
        if (claimTenant.Kind == ClaimTenantKind.Invalid)
        {
            throw new TenantClaimInvalidException();
        }

        if (claimTenant.Kind == ClaimTenantKind.Valid && claimTenant.Tenant != sessionTenant)
        {
            throw new InvalidOperationException("Caller principal tenant does not match session tenant.");
        }

        var fallbackUserSid = string.IsNullOrWhiteSpace(sessionContext.ServicePrincipalId)
            ? (Sid?)null
            : new Sid(sessionContext.ServicePrincipalId);

        var callerSecurityContext = ResolveCallerSecurityContext(
            principal,
            fallbackTenant: sessionTenant,
            fallbackUserSid: fallbackUserSid,
            allowGroups: !string.IsNullOrWhiteSpace(sessionContext.UserSid));

        requestBuilder.SetGlobalState(nameof(ClaimsPrincipal), principal);
        requestBuilder.SetGlobalState(nameof(CallerSecurityContext), callerSecurityContext);
        requestBuilder.Features.Set(McpSessionStateMarker.Instance);
    }

    private static bool HasGlobalStateKey(OperationRequestBuilder requestBuilder, string key) =>
        ContainsStateKey(BuilderContextDataField?.GetValue(requestBuilder), key) ||
        ContainsStateKey(BuilderReadOnlyContextDataField?.GetValue(requestBuilder), key);

    private static bool ContainsStateKey(object? stateContainer, string key) => stateContainer switch
    {
        null => false,
        ImmutableOrderedDictionary<string, object?>.Builder builder => builder.ContainsKey(key),
        IReadOnlyDictionary<string, object?> roDict => roDict.ContainsKey(key),
        IDictionary<string, object?> rwDict => rwDict.ContainsKey(key),
        System.Collections.IDictionary legacyDict => legacyDict.Contains(key),
        _ => false
    };

    public static bool HasSessionPrincipalOverride(IResolverContext? resolverContext) =>
        resolverContext is not null &&
        (resolverContext.ContextData.ContainsKey(nameof(ClaimsPrincipal)) ||
         resolverContext.ScopedContextData.ContainsKey(nameof(ClaimsPrincipal)));

    public static ClaimsPrincipal Resolve(IResolverContext? resolverContext, IHttpContextAccessor? httpContextAccessor = null)
    {
        if (resolverContext is not null)
        {
            if (resolverContext.ContextData.TryGetValue(nameof(ClaimsPrincipal), out var cpObj) && cpObj is ClaimsPrincipal cp)
            {
                return cp;
            }

            if (resolverContext.ScopedContextData.TryGetValue(nameof(ClaimsPrincipal), out var scpObj) && scpObj is ClaimsPrincipal scp)
            {
                return scp;
            }
        }

        var accessor = httpContextAccessor ?? resolverContext?.Services.GetService<IHttpContextAccessor>();
        return accessor?.HttpContext?.User ?? new ClaimsPrincipal();
    }

    public static ClaimsPrincipal? ResolveOrNull(HotChocolate.Execution.RequestContext context, HttpContext? httpContext = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.ContextData.TryGetValue(nameof(ClaimsPrincipal), out var cpObj) && cpObj is ClaimsPrincipal cp)
        {
            return cp;
        }

        var resolvedHttpContext = httpContext ?? context.RequestServices.GetService<IHttpContextAccessor>()?.HttpContext;
        return resolvedHttpContext?.User;
    }

    public static IServiceProvider? ResolveServices(IResolverContext? resolverContext, IHttpContextAccessor? httpContextAccessor = null) =>
        resolverContext?.Services ?? httpContextAccessor?.HttpContext?.RequestServices;

    public static TenantId ResolveContextTenantId(IResolverContext? resolverContext, IHttpContextAccessor? httpContextAccessor = null)
    {
        // Never inherit HTTP transport tenant overrides when an in-process session principal override is active.
        if (HasSessionPrincipalOverride(resolverContext))
        {
            return default;
        }

        TenantId contextTenant = default;
        var items = httpContextAccessor?.HttpContext?.Items;
        if (items?.TryGetValue(SecurityPrincipalContext.ItemKey, out var secObj) == true && secObj is SecurityPrincipalContext secCtx)
        {
            contextTenant = secCtx.ResolvedTenant ?? default;
        }
        else if (items?.TryGetValue("TenantId", out var tidObj) == true && tidObj is TenantId tid)
        {
            contextTenant = tid;
        }

        return contextTenant;
    }

    public static bool HasGatewayRole(ClaimsPrincipal? principal, GatewayRole role, string? tenantId = null)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (role == GatewayRole.ClusterAdmin)
        {
            return ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        }

        if (!ClusterAdminPolicy.IsCanonicalClusterAdmin(principal))
        {
            return RoleEvaluator.HasRole(StripClusterAdminRoleClaims(principal), role, tenantId);
        }

        return RoleEvaluator.HasRole(principal, role, tenantId);
    }

    private static ClaimsPrincipal StripClusterAdminRoleClaims(ClaimsPrincipal principal)
    {
        bool hasClusterAdminClaim = false;
        foreach (var identity in principal.Identities)
        {
            foreach (var claim in identity.Claims)
            {
                if (IsRoleClaim(claim, identity.RoleClaimType) && ClusterAdminPolicy.IsClusterAdminRole(claim.Value))
                {
                    hasClusterAdminClaim = true;
                    break;
                }
            }

            if (hasClusterAdminClaim)
            {
                break;
            }
        }

        if (!hasClusterAdminClaim)
        {
            return principal;
        }

        return new ClaimsPrincipal(principal.Identities.Select(identity =>
            new ClaimsIdentity(
                identity.Claims.Where(c => !(IsRoleClaim(c, identity.RoleClaimType) && ClusterAdminPolicy.IsClusterAdminRole(c.Value))),
                identity.AuthenticationType,
                identity.NameClaimType,
                identity.RoleClaimType)));
    }

    private static bool IsRoleClaim(Claim claim, string roleClaimType) =>
        string.Equals(claim.Type, roleClaimType, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(claim.Type, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(claim.Type, "roles", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(claim.Type, "role", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(claim.Type, "http://schemas.microsoft.com/ws/2008/06/identity/claims/role", StringComparison.OrdinalIgnoreCase);
}
