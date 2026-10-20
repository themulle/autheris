namespace Autheris.Application.Caching.TableCache.Engines;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Autheris.Domain.Options.TableCache;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Autonomous poller executing periodic extractions and zero-downtime shadow swaps.
/// Conforms to SEC-BL04-07 and WP-TC-04.
/// </summary>
public sealed class PeriodicTableCacheRefresher
{
    private readonly ITableCacheCatalog _catalog;
    private readonly ITableCacheManager _manager;
    private readonly TableCacheOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PeriodicTableCacheRefresher>? _logger;

    public PeriodicTableCacheRefresher(
        ITableCacheCatalog catalog,
        ITableCacheManager manager,
        IOptions<TableCacheOptions> options,
        TimeProvider? timeProvider = null,
        ILogger<PeriodicTableCacheRefresher>? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public async Task ExecuteCycleAsync(CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        var allTables = _options.Tables
            .Where(t => t.RefreshMode == TableCacheRefreshMode.PeriodicInterval)
            .ToList();

        foreach (var target in allTables)
        {
            var table = TableIdentifier.Parse(target.Table.Contains('.') ? target.Table : $"{target.DataSource}.default.{target.Table}");
            int intervalSeconds = target.RefreshIntervalSeconds ?? 300;

            if (_catalog.TryGet(table, out var existing) && existing is not null)
            {
                var elapsed = now - existing.LastRefreshedAt;
                if (elapsed < TimeSpan.FromSeconds(intervalSeconds))
                {
                    continue; // Interval not yet elapsed
                }
            }

            try
            {
                _logger?.LogInformation("Triggering periodic background refresh for table '{Table}'", table);
                await _manager.ForceRefreshAsync(table, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Periodic refresh failed for table '{Table}'", table);
            }
        }
    }
}
