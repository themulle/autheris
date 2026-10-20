namespace Autheris.Domain.Caching.TableCache;

/// <summary>
/// Aggregated statistics and telemetry for the governed table cache.
/// </summary>
public sealed record TableCacheStats(
    long TotalHits,
    long TotalMisses,
    long TotalEvictions,
    long TotalRefreshes,
    long TotalStaleServes,
    long TotalMemoryBytes,
    long TotalDiskBytes,
    int ActiveTableCount);
