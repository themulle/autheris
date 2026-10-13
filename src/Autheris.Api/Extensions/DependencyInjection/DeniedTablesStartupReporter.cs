namespace Autheris.Api.Extensions.DependencyInjection;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy.Lint;
using Autheris.Application.Security;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// SEC-ADG-26 (X1): at startup, lists the tables that the governed SQL pipeline denies (unparsable stored row predicates, dialects that
/// WebSQL does not admit) so that an operator sees a deny at scale before the first request. It reads the access profiles of the configured
/// tenants and the virtual filters; it logs table and policy ids and stable codes only. It runs as background work (the host never waits for the store). A store that cannot be read is a warning, never a
/// failed start (the request path stays fail closed on its own). Correlated consent filters (Casbin) are covered by the pre-deploy lint
/// <c>tools/PolicyPredicateLint</c>, which reads the whole governance store.
/// </summary>
public sealed class DeniedTablesStartupReporter(
    ITableMetadataRepository tables,
    IAccessProfileRepository? profiles,
    IVirtualFilterRepository? virtualFilters,
    IOptions<GatewayOptions> options,
    ILogger<DeniedTablesStartupReporter> logger) : BackgroundService
{
    /// <summary>The listing is background work: the host start never waits for the governance store.</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // the first read of a store can be synchronous; leave the start-up thread before it
        await Task.Yield();
        try
        {
            var catalog = (await tables.GetAllTablesAsync(stoppingToken).ConfigureAwait(false)).Where(static t => t.Table.IsActive).ToList();

            var accessProfiles = new List<AccessProfile>();
            if (profiles is not null)
            {
                var tenants = TenantCollisionGuard.ConfiguredTenantIds(options.Value)
                    .Select(static id => id is not null && TenantId.TryParse(id, out var tenant) ? tenant : (TenantId?)null)
                    .OfType<TenantId>()
                    .Append(TenantId.LegacySingleTenant)
                    .Distinct()
                    .ToList();
                foreach (var tenant in tenants)
                {
                    accessProfiles.AddRange(await profiles.GetAllProfilesAsync(tenant, stoppingToken).ConfigureAwait(false));
                }
            }

            var filters = virtualFilters is null
                ? []
                : (await virtualFilters.LoadSnapshotAsync(stoppingToken).ConfigureAwait(false)).Filters.ToList();

            GatewayStartupValidator.ListDeniedTables(new PolicyLintInput(catalog, accessProfiles, filters, []), logger);
        }
        catch (OperationCanceledException)
        {
            // the host is stopping: nothing to report
        }
        catch (Exception ex)
        {
            // Only the exception type: a store message can echo connection details.
            logger.LogWarning("Governed SQL: the denied-table listing could not be built ({ExceptionType}).", ex.GetType().Name);
        }
    }
}
