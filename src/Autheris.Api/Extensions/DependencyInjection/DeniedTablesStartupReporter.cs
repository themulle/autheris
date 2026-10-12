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
/// tenants and the virtual filters; it logs table and policy ids and stable codes only. A store that cannot be read is a warning, never a
/// failed start (the request path stays fail closed on its own). Correlated consent filters (Casbin) are covered by the pre-deploy lint
/// <c>tools/PolicyPredicateLint</c>, which reads the whole governance store.
/// </summary>
public sealed class DeniedTablesStartupReporter(
    ITableMetadataRepository tables,
    IAccessProfileRepository? profiles,
    IVirtualFilterRepository? virtualFilters,
    IOptions<GatewayOptions> options,
    ILogger<DeniedTablesStartupReporter> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var catalog = (await tables.GetAllTablesAsync(cancellationToken).ConfigureAwait(false)).Where(static t => t.Table.IsActive).ToList();

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
                    accessProfiles.AddRange(await profiles.GetAllProfilesAsync(tenant, cancellationToken).ConfigureAwait(false));
                }
            }

            var filters = virtualFilters is null
                ? []
                : (await virtualFilters.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false)).Filters.ToList();

            GatewayStartupValidator.ListDeniedTables(new PolicyLintInput(catalog, accessProfiles, filters, []), logger);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Only the exception type: a store message can echo connection details.
            logger.LogWarning("Governed SQL: the denied-table listing could not be built ({ExceptionType}).", ex.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
