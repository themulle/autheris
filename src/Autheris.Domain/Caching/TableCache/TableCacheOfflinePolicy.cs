namespace Autheris.Domain.Caching.TableCache;

/// <summary>
/// Declares behavior when upstream data source is unreachable or in outage.
/// </summary>
public enum TableCacheOfflinePolicy
{
    /// <summary>Fail-closed: Returns 503 Service Unavailable if cache is expired or unverified.</summary>
    FailClosed = 1,

    /// <summary>High-availability: Serves stale cached data with X-Autheris-Cache: Stale-Offline header.</summary>
    ServeStaleWithWarning = 2
}
