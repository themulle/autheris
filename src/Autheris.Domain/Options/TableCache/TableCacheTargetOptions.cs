namespace Autheris.Domain.Options.TableCache;

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Caching.TableCache;

/// <summary>
/// Configuration for an individual cached table target.
/// </summary>
public sealed class TableCacheTargetOptions
{
    [Required]
    public string DataSource { get; init; } = string.Empty;

    [Required]
    public string Table { get; init; } = string.Empty;

    public TableCacheRefreshMode RefreshMode { get; init; } = TableCacheRefreshMode.TtlExpiry;

    public TableCacheStorageTier StorageTier { get; init; } = TableCacheStorageTier.DuckDbDisk;

    public IReadOnlyList<string>? Columns { get; init; }

    public string? FilterPredicate { get; init; }

    public int? TtlSeconds { get; init; }

    public int? RefreshIntervalSeconds { get; init; }

    public string? CronExpression { get; init; }

    public long? MaxRows { get; init; }

    public TableCacheOfflinePolicy OfflinePolicy { get; init; } = TableCacheOfflinePolicy.FailClosed;

    public double ProactiveEarlyRefreshRatio { get; init; } = 0.10; // Refresh at 90% elapsed TTL
}
