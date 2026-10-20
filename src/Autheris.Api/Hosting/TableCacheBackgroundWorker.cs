namespace Autheris.Api.Hosting;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching.TableCache.Engines;
using Autheris.Domain.Options.TableCache;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Hosted background service managing periodic table cache refreshing and TTL sweeps.
/// </summary>
public sealed class TableCacheBackgroundWorker : BackgroundService
{
    private readonly PeriodicTableCacheRefresher _refresher;
    private readonly TtlTableCacheInvalidator _ttlInvalidator;
    private readonly TableCacheOptions _options;
    private readonly ILogger<TableCacheBackgroundWorker>? _logger;

    public TableCacheBackgroundWorker(
        PeriodicTableCacheRefresher refresher,
        TtlTableCacheInvalidator ttlInvalidator,
        IOptions<TableCacheOptions> options,
        ILogger<TableCacheBackgroundWorker>? logger = null)
    {
        _refresher = refresher ?? throw new ArgumentNullException(nameof(refresher));
        _ttlInvalidator = ttlInvalidator ?? throw new ArgumentNullException(nameof(ttlInvalidator));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger?.LogInformation("Governed Table Cache is disabled. Background worker idle.");
            return;
        }

        _logger?.LogInformation("Governed Table Cache background worker started.");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await _refresher.ExecuteCycleAsync(stoppingToken).ConfigureAwait(false);
                await _ttlInvalidator.SweepExpiredAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Exception in TableCacheBackgroundWorker cycle.");
            }
        }

        _logger?.LogInformation("Governed Table Cache background worker stopped.");
    }
}
