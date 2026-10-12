namespace Autheris.Extensions.DataCatalog;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class DataCatalogSyncBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<DataCatalogSyncBackgroundService> _logger;
    private readonly Autheris.Application.State.IDistributedClusterStateProvider? _clusterState;

    public DataCatalogSyncBackgroundService(
        IServiceProvider serviceProvider,
        IOptions<GatewayOptions> options,
        ILogger<DataCatalogSyncBackgroundService> logger,
        Autheris.Application.State.IDistributedClusterStateProvider? clusterState = null)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _logger = logger;
        _clusterState = clusterState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var catalogOpts = _options.Value.Catalog;
        if (!catalogOpts.Enabled)
        {
            _logger.LogInformation("Data Catalog integration is disabled. Background sync worker will not run.");
            return;
        }

        _logger.LogInformation(
            "Starting Data Catalog background synchronization worker. Provider: {Provider}, Interval: {Interval} min",
            catalogOpts.Provider, catalogOpts.SyncIntervalMinutes);

        // Run an initial sync after a brief startup delay
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);

        var leaseDuration = TimeSpan.FromMinutes(Math.Max(5, catalogOpts.SyncIntervalMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                IAsyncDisposable? syncLock = null;
                if (_clusterState != null)
                {
                    syncLock = await _clusterState.TryAcquireLockAsync("catalog:datacatalog:sync", leaseDuration, stoppingToken).ConfigureAwait(false);
                    if (syncLock == null)
                    {
                        _logger.LogDebug("Data Catalog sync lock held by another cluster replica; skipping cycle.");
                        var skipMinutes = Math.Clamp(_options.Value.Catalog.SyncIntervalMinutes, 1, 1440);
                        await Task.Delay(TimeSpan.FromMinutes(skipMinutes), stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                }

                await using (syncLock)
                {
                    using var scope = _serviceProvider.CreateScope();
                    var syncService = scope.ServiceProvider.GetRequiredService<IDataCatalogSyncService>();

                    var result = await syncService.SyncCatalogAsync(dryRun: false, ct: stoppingToken).ConfigureAwait(false);
                    if (result.Success)
                    {
                        _logger.LogInformation(
                            "Periodic Data Catalog sync succeeded. Synced {Tables} tables, {Columns} columns, {Art9} Art-9 GDPR protected tables.",
                            result.SyncedTablesCount, result.SyncedColumnsCount, result.Art9ProtectedTablesCount);
                    }
                    else
                    {
                        _logger.LogWarning("Periodic Data Catalog sync completed with warnings: {Warnings}", string.Join("; ", result.Warnings));
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in periodic Data Catalog synchronization worker.");
            }

            var delayMinutes = Math.Clamp(_options.Value.Catalog.SyncIntervalMinutes, 1, 1440);
            await Task.Delay(TimeSpan.FromMinutes(delayMinutes), stoppingToken).ConfigureAwait(false);
        }
    }
}
