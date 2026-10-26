namespace Autheris.Application.Catalog.Services;

using System;
using System.Collections.Concurrent;
using System.Security.Claims;
using Autheris.Domain.Common;
using Autheris.Domain.Tenancy;
using Microsoft.Extensions.Logging;

/// <summary>
/// Throttled diagnostic for the one silent downgrade of the catalog scope: a principal that holds the GovernanceAdmin role but carries
/// no tenant claim while no default tenant is configured has no catalog standing. In a single-tenant installation that is a
/// misconfiguration, so the log names the option. At most one Warning per principal and window; the per-process table
/// is bounded.
/// </summary>
public static class CallerScopeDiagnostics
{
    private const int MaxTrackedPrincipals = 1024;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, long> LastReportedTicks = new(StringComparer.Ordinal);

    public static void ReportIfClaimlessAdmin(ILogger logger, ClaimsPrincipal? user, CallerScope scope, long? nowTicks = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (scope != CallerScope.None || user?.Identity?.IsAuthenticated != true || user.GetUserSid() == null ||
            TenantClaimReader.Read(user).Kind != ClaimTenantKind.Absent || !CallerScopeResolver.HoldsGovernanceAdminRole(user))
        {
            return;
        }

        var key = user.GetUserSid()?.Value ?? user?.Identity?.Name ?? "?";
        var now = nowTicks ?? Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
        if (LastReportedTicks.Count >= MaxTrackedPrincipals && !LastReportedTicks.ContainsKey(key))
        {
            LastReportedTicks.Clear();
        }

        var last = LastReportedTicks.GetOrAdd(key, long.MinValue);
        if (last != long.MinValue && now - last < Window.Ticks)
        {
            return;
        }

        LastReportedTicks[key] = now;
        logger.LogWarning(
            "A principal with the GovernanceAdmin role has no tenant claim and no default tenant is configured: it has no catalog standing. " +
            "Single-tenant deployments must set Gateway:Tenancy:DefaultTenantId.");
    }
}
