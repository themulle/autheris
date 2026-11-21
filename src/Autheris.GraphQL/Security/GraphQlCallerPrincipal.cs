namespace Autheris.GraphQL.Security;

using System;
using System.Security.Claims;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Security;
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

    public static ClaimsPrincipal? ResolveOrNull(RequestContext context, HttpContext? httpContext = null)
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

    public static bool HasGatewayRole(ClaimsPrincipal? principal, GatewayRole role, string? tenantId = null) =>
        role == GatewayRole.ClusterAdmin
            ? ClusterAdminPolicy.IsCanonicalClusterAdmin(principal)
            : RoleEvaluator.HasRole(principal, role, tenantId);
}
