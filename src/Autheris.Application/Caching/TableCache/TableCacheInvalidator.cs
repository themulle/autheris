namespace Autheris.Application.Caching.TableCache;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Caching.TableCache;
using Autheris.Domain.Common;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles invalidation and eviction of cached tables and data sources.
/// </summary>
public sealed class TableCacheInvalidator : ITableCacheInvalidator
{
    private readonly ITableCacheCatalog _catalog;
    private readonly ITableCacheStore _store;
    private readonly ILogger<TableCacheInvalidator>? _logger;

    public TableCacheInvalidator(
        ITableCacheCatalog catalog,
        ITableCacheStore store,
        ILogger<TableCacheInvalidator>? logger = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger;
    }

    public async Task InvalidateAsync(TableIdentifier table, string reason, CancellationToken ct = default)
    {
        _logger?.LogInformation("Invalidating table cache for '{Table}'. Reason: {Reason}", table, reason);

        _catalog.Remove(table);
        await _store.EvictAsync(table, ct).ConfigureAwait(false);
    }

    public async Task InvalidateDataSourceAsync(string dataSource, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataSource);
        _logger?.LogInformation("Invalidating table cache for data source '{DataSource}'. Reason: {Reason}", dataSource, reason);

        var matchingTables = _catalog.GetAll()
            .Where(m => string.Equals(m.Table.Domain, dataSource, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Table)
            .ToList();

        foreach (var table in matchingTables)
        {
            _catalog.Remove(table);
            await _store.EvictAsync(table, ct).ConfigureAwait(false);
        }
    }
}
