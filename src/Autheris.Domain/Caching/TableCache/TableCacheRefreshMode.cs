namespace Autheris.Domain.Caching.TableCache;

/// <summary>
/// Declares the synchronization mechanism used to keep the cached table updated.
/// </summary>
public enum TableCacheRefreshMode
{
    /// <summary>Subscribes to ICdcStreamStore to apply real-time delta mutations.</summary>
    CdcStreaming = 1,

    /// <summary>Periodically queries upstream source on interval/cron into shadow staging table.</summary>
    PeriodicInterval = 2,

    /// <summary>Invalidates on TTL expiration and reloads on read via single-flight lock.</summary>
    TtlExpiry = 3
}
