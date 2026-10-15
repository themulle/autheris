namespace Autheris.Extensions.DataCatalog;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Hosting;
using Autheris.Application.State;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Periodic Data Catalog sync, run by exactly one cluster replica at a time under a renewed lease.
/// The sync is idempotent and therefore not fenced: a replica that loses its lease stops through the job's cancellation token.
/// </summary>
public sealed class DataCatalogSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<GatewayOptions> options,
    IDistributedClusterStateProvider clusterState,
    ILogger<DataCatalogSyncBackgroundService> logger,
    TimeProvider? timeProvider = null)
    : ClusterScheduledService<DataCatalogSyncJob>(scopeFactory, clusterState, logger, timeProvider)
{
    protected override string LeaseKey => "catalog:datacatalog:sync";

    protected override ClusterSchedule GetSchedule()
    {
        var catalog = options.CurrentValue.Catalog;
        var interval = TimeSpan.FromMinutes(Math.Clamp(catalog.SyncIntervalMinutes, 1, 1440));
        return new ClusterSchedule(
            catalog.Enabled,
            interval,
            InitialDelay: TimeSpan.FromSeconds(15),
            LeaseTtl: TimeSpan.FromMinutes(2),
            MaxJitter: interval / 10);
    }
}

/// <summary>One Data Catalog synchronization run.</summary>
public sealed class DataCatalogSyncJob(IDataCatalogSyncService syncService, ILogger<DataCatalogSyncJob> logger) : IClusterScheduledJob
{
    public async Task ExecuteAsync(ClusterJobContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var result = await syncService.SyncCatalogAsync(dryRun: false, ct: ct).ConfigureAwait(false);
        if (result.Success)
        {
            logger.LogInformation(
                "Periodic Data Catalog sync succeeded. Synced {Tables} tables, {Columns} columns, {Art9} Art-9 GDPR protected tables.",
                result.SyncedTablesCount, result.SyncedColumnsCount, result.Art9ProtectedTablesCount);
        }
        else
        {
            logger.LogWarning("Periodic Data Catalog sync completed with warnings: {Warnings}", string.Join("; ", result.Warnings));
        }
    }
}
