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
    ILogger<GatewayRoleRightAssignmentReport> logger) : IHostedService
{
    /// <summary>The most role names listed in one log line; the total count is always logged.</summary>
    internal const int MaxListedRoles = 50;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var realm in realmRegistry.GetAllRealms())
            {
                var mappings = await repository.GetMappingsAsync(TenantId.LegacySingleTenant, realm.RealmId, cancellationToken).ConfigureAwait(false);
                foreach (var mapping in mappings.Where(m => m.IsApproved))
                {
                    assigned.Add(mapping.GroupOrRole);
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
