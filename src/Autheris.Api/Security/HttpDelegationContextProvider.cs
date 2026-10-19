namespace Autheris.Api.Security;

using System;
using System.Diagnostics;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;

/// <summary>
/// Issue #238 (PLAN-DATA-238, WP-SC-15): Resolves ambient caller token, principal, and correlation headers from HttpContext.
/// </summary>
public sealed class HttpDelegationContextProvider(IHttpContextAccessor? httpContextAccessor = null) : IDelegationContextProvider
{
    public DelegationContext? ResolveDelegationContext()
    {
        var context = httpContextAccessor?.HttpContext;
        if (context is null)
        {
            return null;
        }

        string? token = null;
        if (context.Request.Headers.TryGetValue("Authorization", out var authVals) && authVals.Count > 0)
        {
            var first = authVals[0];
            if (first != null && first.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = first["Bearer ".Length..].Trim();
            }
        }

        TenantId? tenantId = null;
        if (context.Items.TryGetValue(SecurityPrincipalContext.ItemKey, out var secObj) && secObj is SecurityPrincipalContext secCtx)
        {
            tenantId = secCtx.TenantId;
        }

        string? correlationId = null;
        if (context.Request.Headers.TryGetValue("X-Correlation-Id", out var cVals) && cVals.Count > 0)
        {
            correlationId = cVals[0];
        }
        else
        {
            correlationId = context.TraceIdentifier;
        }

        string? traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

        return new DelegationContext(
            CallerToken: token,
            Principal: context.User,
            TenantId: tenantId,
            CorrelationId: correlationId,
            TraceId: traceId);
    }
}
