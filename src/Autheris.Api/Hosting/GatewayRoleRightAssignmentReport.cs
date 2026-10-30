namespace Autheris.Api.Hosting;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Startup report (NL-SC-02): a GatewayRole claim no longer implies any functional right, so a role without a configured functional-right
/// assignment grants nothing. The report names those roles at Warning (names only, bounded) so that a silent privilege loss is visible.
/// It never blocks startup: failing closed is the empty grant, and a store error only produces a Warning.
/// </summary>
public sealed class GatewayRoleRightAssignmentReport(
    IFunctionalRightRepository repository,
    IIdentityRealmRegistry realmRegistry,
    ILogger<GatewayRoleRightAssignmentReport> logger,
    Autheris.Application.Tenancy.ITenantResolver? tenantResolver = null) : IHostedService
{
    /// <summary>The most role names listed in one log line; the total count is always logged.</summary>
    internal const int MaxListedRoles = 50;

    private static IEnumerable<TenantId> TenantsOf(Autheris.Domain.Identity.IdentityRealm realm, TenantId? defaultTenant) => realm.TenantBinding.Kind switch
    {
        Autheris.Domain.Identity.TenantBindingKind.Fixed when realm.TenantBinding.FixedTenantId is { } fixedTenant => [fixedTenant],
        Autheris.Domain.Identity.TenantBindingKind.ClaimMapped when realm.TenantBinding.Mapping is { } map => map.Values.Distinct(),
        Autheris.Domain.Identity.TenantBindingKind.Unbound when defaultTenant is { } d => [d],
        _ => []
    };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var realm in realmRegistry.GetAllRealms())
            {
                // DT-A-9: the realm's own tenants (fixed binding, claim mapping values) or the configured default tenant; none means nothing to read.
                foreach (var tenant in TenantsOf(realm, tenantResolver?.DefaultTenant))
                {
                    var mappings = await repository.GetMappingsAsync(tenant, realm.RealmId, cancellationToken).ConfigureAwait(false);
                    foreach (var mapping in mappings.Where(m => m.IsApproved))
                    {
                        assigned.Add(mapping.GroupOrRole);
                    }
                }
            }

            Report(Enum.GetValues<GatewayRole>().Select(r => r.ToString()), assigned, logger);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The functional-right assignments could not be read; GatewayRole claims grant no functional right without a configured assignment.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static void Report(IEnumerable<string> roles, IReadOnlySet<string> assignedGroupsAndRoles, ILogger logger)
    {
        var unassigned = roles.Where(r => !assignedGroupsAndRoles.Contains(r)).OrderBy(r => r, StringComparer.Ordinal).ToList();
        if (unassigned.Count == 0)
        {
            return;
        }

        logger.LogWarning(
            "{Count} GatewayRole(s) have no configured functional-right assignment and grant no functional right (GatewayRole claims no longer imply rights): {Roles}{More}",
            unassigned.Count,
            string.Join(", ", unassigned.Take(MaxListedRoles)),
            unassigned.Count > MaxListedRoles ? $" and {unassigned.Count - MaxListedRoles} more" : string.Empty);
    }
}
