namespace Autheris.Application.Sql.ResultCache;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// The WebSQL result cache. It only skips source execution: callers authenticate, authorize, rewrite and audit every request
/// first. A hit needs an equal tenant and user, an unexpired entry and unchanged governance and table epochs.
/// </summary>
public interface IWebSqlResultCache
{
    /// <summary>True when the byte gate is above zero and the epoch sources are available.</summary>
    bool IsEnabled { get; }

    /// <summary>The current caps; <see cref="ResultCacheLimits.Off"/> when the gate is closed.</summary>
    ResultCacheLimits Limits { get; }

    ValueTask<WebSqlResultCacheLookup> LookupAsync(WebSqlResultCacheKey key, bool bypass, TimeSpan followerWait, CancellationToken ct);

    bool IsMarkedTooLarge(string hash);

    /// <summary>Stores <paramref name="result"/> when the epochs did not change since the lookup. Sets <see cref="WebSqlResultCacheLookup.Reason"/> when it does not.</summary>
    ValueTask<bool> TryCommitAsync(
        WebSqlResultCacheLookup fill, CachedWebSqlResult result, TimeSpan ttl, string dataSource, string? traceId, CancellationToken ct);

    /// <summary>Remembers that the full statement of a paged request is too large to cache, so later pages skip the attempt.</summary>
    void MarkTooLarge(WebSqlResultCacheLookup fill, TimeSpan ttl);

    /// <summary>Memory for a recording of the key's user; null when the cache is off. Dispose it when the recording ends.</summary>
    IResultCacheReservation? TryReserve(WebSqlResultCacheKey key);
}

public sealed partial class WebSqlResultCache : IWebSqlResultCache, IDisposable
{
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly GovernanceEpochProvider? _governance;
    private readonly IEpochValidationService? _epochs;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly WebSqlResultCacheStore _store;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Flight> _flights = new(StringComparer.Ordinal);
    private ResultCacheLimits _limits = ResultCacheLimits.Off;

    public WebSqlResultCache(
        IOptionsMonitor<GatewayOptions> options,
        GovernanceEpochProvider? governanceEpochs = null,
        IEpochValidationService? epochValidation = null,
        TimeProvider? timeProvider = null,
        ILogger<WebSqlResultCache>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _governance = governanceEpochs;
        _epochs = epochValidation;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _store = new WebSqlResultCacheStore(_time);
    }

    public bool IsEnabled => Limits.Enabled && _governance is not null && _epochs is not null;

    public ResultCacheLimits Limits
    {
        get
        {
            var limits = ResultCacheLimits.FromOptions(_options.CurrentValue.WebSql);
            lock (_gate)
            {
                if (!limits.Equals(_limits))
                {
                    _limits = limits;
                    _store.Configure(limits);
                }
            }

            return limits;
        }
    }

    public async ValueTask<WebSqlResultCacheLookup> LookupAsync(
        WebSqlResultCacheKey key, bool bypass, TimeSpan followerWait, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!Limits.Enabled)
        {
            return WebSqlResultCacheLookup.ForUncached(WebSqlResultCacheStatus.Ineligible, WebSqlResultCacheReasons.SourceDisabled);
        }

        var snapshot = await CaptureAsync(key, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return Unavailable();
        }

        if (!bypass && TryServe(key, snapshot) is { } hit)
        {
            return WebSqlResultCacheLookup.ForHit(hit, snapshot);
        }

        Flight? follow = null;
        Flight? mine = null;
        lock (_gate)
        {
            if (_flights.TryGetValue(key.Hash, out var running))
            {
                follow = bypass ? null : running;
            }
            else
            {
                mine = new Flight();
                _flights[key.Hash] = mine;
            }
        }

        if (follow is not null)
        {
            return await FollowAsync(key, follow, followerWait, ct).ConfigureAwait(false);
        }

        return WebSqlResultCacheLookup.ForLeader(key, bypass, snapshot, mine is null ? null : () => Complete(key.Hash, mine));
    }

    public bool IsMarkedTooLarge(string hash) => Limits.Enabled && _store.IsMarkedTooLarge(hash);

    public async ValueTask<bool> TryCommitAsync(
        WebSqlResultCacheLookup fill, CachedWebSqlResult result, TimeSpan ttl, string dataSource, string? traceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fill);
        ArgumentNullException.ThrowIfNull(result);
        if (!fill.IsFillLeader || fill.Snapshot is null || fill.Key is not { } key)
        {
            return false;
        }

        if (!IsEnabled || ttl <= TimeSpan.Zero)
        {
            fill.Reason = WebSqlResultCacheReasons.SourceDisabled;
            return false;
        }

        var ceiling = TimeSpan.FromSeconds(WebSqlOptions.ResultCacheHardMaxTtlSeconds);
        var lifetime = ttl > ceiling ? ceiling : ttl;
        try
        {
            var after = await CaptureAsync(key, ct).ConfigureAwait(false);
            if (after is null)
            {
                fill.Reason = WebSqlResultCacheReasons.EpochUnavailable;
                return false;
            }

            if (ResultCacheEpochSnapshot.FindInvalidation(fill.Snapshot, after, _epochs!, key.Tables) is not null)
            {
                fill.Reason = WebSqlResultCacheReasons.GovernanceChanged;
                return false;
            }

            var now = _time.GetUtcNow();
            var entry = new ResultCacheEntry(key.Hash, key.Tenant, key.UserSid, key.Tables, result, fill.Snapshot, now, now + lifetime, traceId);
            if (!_store.TryPut(entry))
            {
                fill.Reason = WebSqlResultCacheReasons.TooLarge;
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger is not null)
            {
                LogCommitFailed(_logger, ex.GetType().Name);
            }

            fill.Reason = WebSqlResultCacheReasons.Incomplete;
            return false;
        }

        WebSqlResultCacheMetrics.RecordRequest(dataSource, WebSqlResultCacheStatus.Store);
        return true;
    }

    public void MarkTooLarge(WebSqlResultCacheLookup fill, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(fill);
        if (!fill.IsFillLeader || fill.Key is not { } key || !IsEnabled)
        {
            return;
        }

        var ceiling = TimeSpan.FromSeconds(WebSqlOptions.ResultCacheHardMaxTtlSeconds);
        _store.MarkTooLarge(key.Hash, ttl > ceiling ? ceiling : ttl);
    }

    public IResultCacheReservation? TryReserve(WebSqlResultCacheKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return IsEnabled ? _store.BeginReservation(key.Tenant, key.UserSid) : null;
    }

    internal int EntryCount => _store.EntryCount;

    public void Dispose() => _store.Dispose();

    private WebSqlResultCacheLookup Unavailable()
    {
        if (_logger is not null)
        {
            LogUnavailable(_logger, WebSqlResultCacheReasons.EpochUnavailable);
        }

        return WebSqlResultCacheLookup.ForUncached(WebSqlResultCacheStatus.Ineligible, WebSqlResultCacheReasons.EpochUnavailable);
    }

    private ValueTask<ResultCacheEpochSnapshot?> CaptureAsync(WebSqlResultCacheKey key, CancellationToken ct) =>
        ResultCacheEpochSnapshot.TryCaptureAsync(_governance, _epochs, key.Tenant, key.Tables, ct);

    private WebSqlResultCacheHit? TryServe(WebSqlResultCacheKey key, ResultCacheEpochSnapshot current)
    {
        var entry = _store.TryGet(key.Hash, key.Tenant, key.UserSid);
        if (entry is null)
        {
            return null;
        }

        if (ResultCacheEpochSnapshot.FindInvalidation(entry.Snapshot, current, _epochs!, entry.Tables) is { } reason)
        {
            _store.Invalidate(entry, reason);
            return null;
        }

        var age = (int)Math.Clamp((_time.GetUtcNow() - entry.StoredAt).TotalSeconds, 0, int.MaxValue);
        return new WebSqlResultCacheHit(entry.Result, age, entry.TraceId);
    }

    private async ValueTask<WebSqlResultCacheLookup> FollowAsync(
        WebSqlResultCacheKey key, Flight leader, TimeSpan wait, CancellationToken ct)
    {
        try
        {
            await leader.Completion.Task.WaitAsync(wait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return WebSqlResultCacheLookup.ForUncached(WebSqlResultCacheStatus.Miss, null);
        }

        var snapshot = await CaptureAsync(key, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return Unavailable();
        }

        return TryServe(key, snapshot) is { } hit
            ? WebSqlResultCacheLookup.ForHit(hit, snapshot)
            : WebSqlResultCacheLookup.ForUncached(WebSqlResultCacheStatus.Miss, null, snapshot);
    }

    private void Complete(string hash, Flight flight)
    {
        lock (_gate)
        {
            if (_flights.TryGetValue(hash, out var registered) && ReferenceEquals(registered, flight))
            {
                _flights.Remove(hash);
            }
        }

        flight.Completion.TrySetResult();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Result cache unavailable: {Reason}")]
    private static partial void LogUnavailable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Result cache commit failed: {ExceptionType}")]
    private static partial void LogCommitFailed(ILogger logger, string exceptionType);

    private sealed class Flight
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
