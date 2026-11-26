namespace Autheris.Application.Sql.ResultCache;

using System;
using System.Threading;

/// <summary>A served cache entry with its age and the trace id of the execution that stored it.</summary>
public sealed record WebSqlResultCacheHit(CachedWebSqlResult Result, int AgeSeconds, string? StoredTraceId);

/// <summary>
/// The outcome of a lookup. A fill leader may commit one result and must dispose the lookup when it is done (also on failure or
/// cancellation) so waiting requests are released.
/// </summary>
public sealed class WebSqlResultCacheLookup : IDisposable
{
    private readonly Action? _release;
    private int _released;

    private WebSqlResultCacheLookup(
        WebSqlResultCacheStatus status, WebSqlResultCacheHit? hit, ResultCacheEpochSnapshot? snapshot, bool isFillLeader, string? reason, Action? release)
    {
        Status = status;
        Hit = hit;
        Snapshot = snapshot;
        IsFillLeader = isFillLeader;
        Reason = reason;
        _release = release;
    }

    public WebSqlResultCacheStatus Status { get; }

    public WebSqlResultCacheHit? Hit { get; }

    /// <summary>The stamp read before the source executes; null when the epochs were unavailable.</summary>
    public ResultCacheEpochSnapshot? Snapshot { get; }

    /// <summary>True when this request may commit its result.</summary>
    public bool IsFillLeader { get; }

    /// <summary>A fixed reason code explaining a non-hit, or why the commit did not store.</summary>
    public string? Reason { get; internal set; }

    internal WebSqlResultCacheKey? Key { get; private init; }

    internal static WebSqlResultCacheLookup ForHit(WebSqlResultCacheHit hit, ResultCacheEpochSnapshot snapshot) =>
        new(WebSqlResultCacheStatus.Hit, hit, snapshot, false, null, null);

    internal static WebSqlResultCacheLookup ForLeader(WebSqlResultCacheKey key, bool bypass, ResultCacheEpochSnapshot snapshot, Action? release) =>
        new(bypass ? WebSqlResultCacheStatus.Bypass : WebSqlResultCacheStatus.Miss, null, snapshot, true, null, release) { Key = key };

    internal static WebSqlResultCacheLookup ForUncached(WebSqlResultCacheStatus status, string? reason, ResultCacheEpochSnapshot? snapshot = null) =>
        new(status, null, snapshot, false, reason, null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _release?.Invoke();
        }
    }
}
