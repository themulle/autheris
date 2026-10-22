using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Autheris.Infrastructure.Cache;

public sealed class EpochValidationService : IEpochValidationService, IHostedService, IDisposable
{
    private sealed record LocalEpochEntry(long Epoch, DateTimeOffset CachedAt);

    private readonly ConcurrentDictionary<string, long> _epochs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastEpochRefresh = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LocalEpochEntry> _microCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly EpochValidationOptions _options;
    private readonly TimeProvider _time;
    private readonly IEventBus _eventBus;
    private readonly string _invalidationChannel;
    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly string _redisPrefix;
    private readonly ConcurrentDictionary<string, long> _highestSeenRedisEpoch = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastRollbackWarningTime = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<EpochValidationService>? _logger;
    private readonly ITableSensitivityLookup _sensitivityLookup;
    private readonly object _subscriptionLock = new();
    private IDisposable? _invalidationSubscription;

    // SEC (lost revoke on Redis failure): a local invalidation whose Redis INCR has not been confirmed yet. Value = generation,
    // so a re-invalidation during a running retry is not cleared by that retry. While a key is pending, cached decisions for
    // it are stale on this node and sensitive tables bypass the cache (same fail-closed rule as the degraded mode).
    private readonly ConcurrentDictionary<string, long> _pendingRedisBumps = new(StringComparer.OrdinalIgnoreCase);
    private long _pendingBumpGeneration;
    private readonly SemaphoreSlim _bumpFlushLock = new(1, 1);

    // Per-key floor recorded when an invalidation is made or received: a Redis epoch <= floor did not move since then (the
    // originating INCR was lost) and is never accepted - it is lifted above the floor (same atomic Lua as the rollback
    // recovery) so the local epoch is never lowered below the post-invalidation value by a Redis read.
    private readonly ConcurrentDictionary<string, long> _epochFloors = new(StringComparer.OrdinalIgnoreCase);

    // Own invalidations whose event-bus echo has not been received yet; the echo must not raise a floor (the originating node
    // already holds the floor and the pending bump), otherwise every revoke would cause a spurious second epoch bump.
    private readonly ConcurrentDictionary<string, int> _ownInvalidationsInFlight = new(StringComparer.OrdinalIgnoreCase);

    // INF-01 R1: Grace period after reconnect during which Redis reads are treated as non-authoritative
    private DateTimeOffset _reconnectGraceUntil = DateTimeOffset.MinValue;

    private const string RollbackLuaScript = @"
local current = redis.call('GET', KEYS[1])
local currentNum = tonumber(current)
local minRequired = tonumber(ARGV[1])
if not currentNum then
    local newEpoch = redis.call('INCR', KEYS[1])
    if newEpoch < minRequired then
        redis.call('SET', KEYS[1], minRequired)
        return minRequired
    end
    return newEpoch
elseif currentNum < minRequired then
    redis.call('SET', KEYS[1], minRequired)
    return minRequired
else
    return currentNum
end";

    // SEC H-01: Epoch values read from Redis/Garnet are not trusted blindly. Each node remembers the highest
    // epoch it has observed per table; a lower value (Redis restart without persistence, or a tampered key that
    // tries to re-validate an older, still-signed L2 entry) is treated as a rollback and forces a fresh epoch
    // above the highest observed one. Residual risk: a node that never observed the higher epoch cannot detect
    // the rollback, therefore Redis/Garnet must additionally be protected by authentication (see GarnetServerManager).
    public EpochValidationService(
        IOptions<GatewayOptions>? options = null,
        IEventBus? eventBus = null,
        ITableSensitivityLookup? sensitivityLookup = null,
        IConnectionMultiplexer? multiplexer = null,
        ILogger<EpochValidationService>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;
        _options = options?.Value?.Caching?.EpochValidation ?? new EpochValidationOptions();
        _invalidationChannel = options?.Value?.Caching?.Redis?.InvalidationChannel ?? "consent:invalidations";
        _eventBus = eventBus ?? new Messaging.InProcessChannelEventBus();
        _sensitivityLookup = sensitivityLookup ?? new Application.Services.TableMetadataSensitivityLookup(() => null);
        _multiplexer = multiplexer;

        if (_multiplexer != null)
        {
            _multiplexer.ConnectionRestored += (sender, args) => _ = OnConnectionRestoredAsync();
        }

        var prefix = options?.Value?.Caching?.Redis?.InstanceName ?? "autheris:";
        _redisPrefix = prefix.EndsWith(':') ? prefix : prefix + ":";
    }

    /// <summary>
    /// POL-9: evicts the local epoch state on reconnect. Floors and pending bumps survive the eviction; every pending bump is
    /// written to Redis now (an INCR on reconnect also invalidates entries that peers cached at the unmoved epoch meanwhile).
    /// </summary>
    internal async Task OnConnectionRestoredAsync()
    {
        try
        {
            _logger?.LogInformation("Redis connection restored. Evicting local L1 epoch cache to synchronize with cluster (POL-9).");
            _reconnectGraceUntil = _time.GetUtcNow() + _options.ReconnectEpochGrace;
            _epochs.Clear();
            _lastEpochRefresh.Clear();
            _microCache.Clear();
            _lastRollbackWarningTime.Clear();
            EnsureSubscribed();
            await FlushPendingRedisBumpsAsync(waitForLock: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Retrying pending policy epoch bumps after Redis reconnect failed; they stay pending (fail closed).");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureSubscribed();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_subscriptionLock)
        {
            _invalidationSubscription?.Dispose();
            _invalidationSubscription = null;
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_subscriptionLock)
        {
            _invalidationSubscription?.Dispose();
            _invalidationSubscription = null;
        }
    }

    private void EnsureSubscribed()
    {
        if (_invalidationSubscription != null) return;
        lock (_subscriptionLock)
        {
            if (_invalidationSubscription != null) return;
            try
            {
                _invalidationSubscription = _eventBus.Subscribe<string>(_invalidationChannel, message =>
                {
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        var key = message.ToLowerInvariant();
                        if (_multiplexer != null && !TryConsumeOwnInvalidation(key))
                        {
                            // A peer revoked: until Redis has moved past what this node knew, the Redis epoch is not trusted.
                            RaiseEpochFloor(key);
                        }

                        _epochs.AddOrUpdate(key, 2, (_, current) => current + 1);
                        _lastEpochRefresh[key] = _time.GetUtcNow();
                        _microCache.TryRemove(key, out _);
                    }
                    return Task.CompletedTask;
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to subscribe to invalidation channel {Channel}; will retry on reconnection.", _invalidationChannel);
            }
        }
    }

    public async Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default)
        => (await GetCurrentEpochCoreAsync(table).ConfigureAwait(false)).Epoch;

    /// <summary>
    /// INF-1: <c>Authoritative</c> is false when Redis is configured but the read failed. The local epoch is then
    /// only a fallback and must not confirm a cached decision (another node may have revoked it meanwhile).
    /// </summary>
    private async Task<(long Epoch, bool Authoritative)> GetCurrentEpochCoreAsync(TableIdentifier table)
    {
        EnsureSubscribed();
        var key = table.ToString().ToLowerInvariant();

        if (_multiplexer != null && _multiplexer.IsConnected)
        {
            try
            {
                await FlushPendingRedisBumpsAsync(waitForLock: false).ConfigureAwait(false);
                if (_pendingRedisBumps.ContainsKey(key))
                {
                    // The local revoke is not in Redis yet: the Redis epoch would re-validate entries cached before it.
                    return (_epochs.GetOrAdd(key, 1), false);
                }

                var now = _time.GetUtcNow();
                bool inGrace = now < _reconnectGraceUntil;

                var db = _multiplexer.GetDatabase();
                var redisKey = (RedisKey)$"{_redisPrefix}epoch:{key}";
                var redisVal = await db.StringGetAsync(redisKey).ConfigureAwait(false);

                // Missing, rolled-back or not-advanced-past-the-floor epochs are lifted (never below anything this node has seen).
                var rEpoch = await ReconcileEpochAsync(db, key, redisKey, redisVal).ConfigureAwait(false);
                _epochs[key] = rEpoch;
                _lastEpochRefresh[key] = now;
                return (rEpoch, !inGrace);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Policy epoch read from Redis failed for {Table}; cached decisions are re-evaluated (degraded).", key);
                return (_epochs.GetOrAdd(key, 1), false);
            }
        }

        var epoch = _epochs.GetOrAdd(key, 1);
        _lastEpochRefresh.TryAdd(key, _time.GetUtcNow());
        return (epoch, _multiplexer == null);
    }

    public async Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(
        IEnumerable<TableIdentifier> tables,
        CancellationToken ct = default)
    {
        var tableList = tables.ToList();
        var result = new Dictionary<TableIdentifier, long>(tableList.Count);

        if (_multiplexer != null && _multiplexer.IsConnected && _options.PipelinedMGetEnabled && tableList.Count > 1)
        {
            var checks = tableList.Select(t => new EpochCheck(t, 0, false)).ToList();
            await AreEpochsValidAsync(checks, ct).ConfigureAwait(false);
            foreach (var t in tableList)
            {
                var key = t.ToString().ToLowerInvariant();
                result[t] = _epochs.TryGetValue(key, out var ep) ? ep : 1;
            }
            return result;
        }

        foreach (var t in tableList)
        {
            result[t] = await GetCurrentEpochAsync(t, ct).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>
    /// Issue #107 (SEC-RF107-05, C-6): all-or-nothing authoritative epoch read for the key-set cache. Redis is read for every table on every call (no
    /// staleness budget, no local fallback). <c>null</c> when Redis is configured but not connected, when any read fails or cannot be confirmed, or on any
    /// exception (Warning with the exception type only); it never throws and never returns a partial result. Without Redis (single node) the local epoch
    /// is authoritative.
    /// </summary>
    public async ValueTask<IReadOnlyDictionary<TableIdentifier, long>?> TryGetAuthoritativeEpochsAsync(
        IReadOnlyCollection<TableIdentifier> tables,
        CancellationToken ct = default)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(tables);
            ct.ThrowIfCancellationRequested();
            if (_multiplexer != null && !_multiplexer.IsConnected)
            {
                return null;
            }

            var distinct = tables.Distinct().ToList();
            var reads = new Task<(long Epoch, bool Authoritative)>[distinct.Count];
            for (int i = 0; i < reads.Length; i++)
            {
                reads[i] = GetCurrentEpochCoreAsync(distinct[i]);
            }

            await Task.WhenAll(reads).ConfigureAwait(false);
            var result = new Dictionary<TableIdentifier, long>(distinct.Count);
            for (int i = 0; i < reads.Length; i++)
            {
                var (epoch, authoritative) = reads[i].Result;
                if (!authoritative)
                {
                    return null;
                }

                result[distinct[i]] = epoch;
            }

            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // a client cancellation is not an epoch failure
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Authoritative policy epoch read failed ({ExceptionType}); key-set caching is skipped.", ex.GetType().Name);
            return null;
        }
    }

    public async ValueTask<IReadOnlyDictionary<TableIdentifier, bool>> AreEpochsValidAsync(
        IReadOnlyList<EpochCheck> checks,
        CancellationToken ct = default)
    {
        if (checks == null || checks.Count == 0)
        {
            return new Dictionary<TableIdentifier, bool>();
        }

        var results = new Dictionary<TableIdentifier, bool>(checks.Count);
        bool isDegraded = _multiplexer != null && !_multiplexer.IsConnected;

        if (isDegraded)
        {
            foreach (var check in checks)
            {
                var key = check.Table.ToString().ToLowerInvariant();
                if (_pendingRedisBumps.ContainsKey(key))
                {
                    results[check.Table] = false;
                    continue;
                }

                if (_options.FailClosedOnSensitiveTables)
                {
                    bool isSensitive = check.IsHighlySensitive || await _sensitivityLookup.IsSensitiveAsync(check.Table, ct).ConfigureAwait(false);
                    if (isSensitive)
                    {
                        results[check.Table] = false;
                        continue;
                    }
                }

                if (_lastEpochRefresh.TryGetValue(key, out var lastRefresh))
                {
                    if (_time.GetUtcNow() - lastRefresh > TimeSpan.FromSeconds(_options.DegradedMaxStalenessSeconds))
                    {
                        results[check.Table] = false;
                        continue;
                    }
                }

                var (current, _) = await GetCurrentEpochCoreAsync(check.Table).ConfigureAwait(false);
                results[check.Table] = current == check.CachedEpoch;
            }

            return results;
        }

        if (_multiplexer == null)
        {
            foreach (var check in checks)
            {
                var (current, _) = await GetCurrentEpochCoreAsync(check.Table).ConfigureAwait(false);
                results[check.Table] = current == check.CachedEpoch;
            }

            return results;
        }

        await FlushPendingRedisBumpsAsync(waitForLock: false).ConfigureAwait(false);
        var bumpOutstanding = !_pendingRedisBumps.IsEmpty;

        var now = _time.GetUtcNow();
        var pendingRedisChecks = new List<EpochCheck>();
        var budgetMs = _options.LocalStalenessBudgetMilliseconds;

        foreach (var check in checks)
        {
            var key = check.Table.ToString().ToLowerInvariant();

            // A local revoke that Redis has not confirmed yet: the entry is stale on this node whatever Redis says. While any
            // bump is outstanding, sensitive tables bypass the cache like in the degraded mode.
            if (_pendingRedisBumps.ContainsKey(key) ||
                (bumpOutstanding && _options.FailClosedOnSensitiveTables &&
                 (check.IsHighlySensitive || await _sensitivityLookup.IsSensitiveAsync(check.Table, ct).ConfigureAwait(false))))
            {
                results[check.Table] = false;
                continue;
            }

            // SEC-SC-05: the staleness budget applies to non-sensitive tables of Autheris-enforced sources only. Sensitive
            // tables (flagged by the caller or by the metadata lookup, which fails closed) and delegated sources always read Redis.
            if (!check.IsHighlySensitive && !check.IsDelegatedSource && budgetMs > 0 && !_epochFloors.ContainsKey(key) &&
                _microCache.TryGetValue(key, out var entry) &&
                (now - entry.CachedAt).TotalMilliseconds <= budgetMs)
            {
                _highestSeenRedisEpoch.TryGetValue(key, out var highestSeen);
                if (entry.Epoch >= highestSeen &&
                    !await _sensitivityLookup.IsSensitiveAsync(check.Table, ct).ConfigureAwait(false))
                {
                    results[check.Table] = entry.Epoch == check.CachedEpoch;
                    continue;
                }
            }

            pendingRedisChecks.Add(check);
        }

        if (pendingRedisChecks.Count == 0)
        {
            return results;
        }

        try
        {
            var db = _multiplexer.GetDatabase();

            // SEC-SC-05 / WP-PF-01: single-key GETs only. A multi-key MGET raises CROSSSLOT on Redis Cluster,
            // which would invalidate every multi-table decision. The multiplexer pipelines concurrent single-key
            // commands over one connection per node, so this costs no extra round trips. Any failure fails closed.
            var redisKeys = new RedisKey[pendingRedisChecks.Count];
            for (int i = 0; i < redisKeys.Length; i++)
            {
                redisKeys[i] = $"{_redisPrefix}epoch:{pendingRedisChecks[i].Table.ToString().ToLowerInvariant()}";
            }

            var values = new RedisValue[redisKeys.Length];
            if (_options.PipelinedMGetEnabled && redisKeys.Length > 1)
            {
                var reads = new Task<RedisValue>[redisKeys.Length];
                for (int i = 0; i < reads.Length; i++)
                {
                    reads[i] = db.StringGetAsync(redisKeys[i]);
                }

                await Task.WhenAll(reads).ConfigureAwait(false);
                for (int i = 0; i < reads.Length; i++)
                {
                    values[i] = reads[i].Result;
                }
            }
            else
            {
                for (int i = 0; i < redisKeys.Length; i++)
                {
                    values[i] = await db.StringGetAsync(redisKeys[i]).ConfigureAwait(false);
                }
            }

            for (int i = 0; i < pendingRedisChecks.Count; i++)
            {
                var check = pendingRedisChecks[i];
                var key = check.Table.ToString().ToLowerInvariant();
                var authoritativeEpoch = await ReconcileEpochAsync(db, key, redisKeys[i], values[i]).ConfigureAwait(false);

                _epochs[key] = authoritativeEpoch;
                _lastEpochRefresh[key] = _time.GetUtcNow();

                if (budgetMs > 0 && _microCache.Count < _options.MaxLocalEpochEntries)
                {
                    _microCache[key] = new LocalEpochEntry(authoritativeEpoch, _time.GetUtcNow());
                }

                results[check.Table] = authoritativeEpoch == check.CachedEpoch;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to read policy epochs from Redis; failing closed for pending tables.");
            foreach (var check in pendingRedisChecks)
            {
                results[check.Table] = false;
            }
        }

        return results;
    }

    private async Task<long> ReconcileEpochAsync(IDatabase db, string key, RedisKey redisKey, RedisValue val)
    {
        _highestSeenRedisEpoch.TryGetValue(key, out var highestSeen);
        var hasFloor = _epochFloors.TryGetValue(key, out var floor);
        long parsed = val.HasValue && long.TryParse(val.ToString(), out var p) ? p : 0;
        long authoritativeEpoch;
        if (parsed <= 0 || parsed < highestSeen || (hasFloor && parsed <= floor))
        {
            var minRequired = highestSeen > 0 ? highestSeen + 1 : 1;
            if (hasFloor)
            {
                minRequired = Math.Max(minRequired, floor + 1);
            }

            if (parsed <= 0 && highestSeen <= 0)
            {
                _logger?.LogDebug("Empty policy epoch cache bootstrap for {Table}; initializing epoch to {Restored}.", key, minRequired);
            }
            else if (parsed <= 0 || parsed < highestSeen)
            {
                LogRollbackWarning(key, parsed, highestSeen, minRequired);
            }
            else
            {
                // The invalidation was announced, but its Redis INCR was lost: Redis still holds the pre-revoke epoch.
                _logger?.LogInformation("Policy epoch for {Table} has not advanced past the invalidation (redis={RedisEpoch}, floor={Floor}); forcing epoch {Restored}.", key, parsed, floor, minRequired);
            }

            var evalResult = await db.ScriptEvaluateAsync(
                RollbackLuaScript,
                [redisKey],
                [(RedisValue)minRequired.ToString()]).ConfigureAwait(false);
            authoritativeEpoch = evalResult is not null && !evalResult.IsNull && long.TryParse(evalResult.ToString(), out var scriptVal)
                ? scriptVal
                : minRequired;
        }
        else
        {
            authoritativeEpoch = parsed;
        }

        _highestSeenRedisEpoch.AddOrUpdate(key, authoritativeEpoch, (_, old) => Math.Max(old, authoritativeEpoch));
        if (hasFloor && authoritativeEpoch > floor)
        {
            // Redis has advanced past the invalidation; a newer floor set concurrently is kept.
            _epochFloors.TryRemove(new KeyValuePair<string, long>(key, floor));
        }

        return authoritativeEpoch;
    }

    /// <summary>Records that every epoch this node knew for <paramref name="key"/> is pre-invalidation (call before the local bump).</summary>
    private void RaiseEpochFloor(string key)
    {
        _highestSeenRedisEpoch.TryGetValue(key, out var highestSeen);
        _epochs.TryGetValue(key, out var local);
        var floor = Math.Max(highestSeen, local);
        _epochFloors.AddOrUpdate(key, floor, (_, current) => Math.Max(current, floor));
    }

    private bool TryConsumeOwnInvalidation(string key)
    {
        while (_ownInvalidationsInFlight.TryGetValue(key, out var count))
        {
            var consumed = count <= 1
                ? _ownInvalidationsInFlight.TryRemove(new KeyValuePair<string, int>(key, count))
                : _ownInvalidationsInFlight.TryUpdate(key, count - 1, count);
            if (consumed)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Writes every pending local invalidation to Redis (INCR, lifted above the floor if Redis was rolled back). A key leaves the
    /// pending set only after Redis confirmed it. Read paths do not wait for a running flush (the key simply stays stale).
    /// </summary>
    private async Task FlushPendingRedisBumpsAsync(bool waitForLock)
    {
        if (_multiplexer == null || _pendingRedisBumps.IsEmpty)
        {
            return;
        }

        if (waitForLock)
        {
            await _bumpFlushLock.WaitAsync().ConfigureAwait(false);
        }
        else if (!await _bumpFlushLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            foreach (var pending in _pendingRedisBumps.ToArray())
            {
                if (await TryBumpRedisEpochAsync(pending.Key, logFailureAsWarning: waitForLock).ConfigureAwait(false))
                {
                    _pendingRedisBumps.TryRemove(pending);
                }
            }
        }
        finally
        {
            _bumpFlushLock.Release();
        }
    }

    private async Task<bool> TryBumpRedisEpochAsync(string key, bool logFailureAsWarning)
    {
        try
        {
            if (_multiplexer == null || !_multiplexer.IsConnected)
            {
                return false;
            }

            var db = _multiplexer.GetDatabase();
            var redisKey = (RedisKey)$"{_redisPrefix}epoch:{key}";
            var incremented = await db.StringIncrementAsync(redisKey).ConfigureAwait(false);

            _highestSeenRedisEpoch.TryGetValue(key, out var highestSeen);
            var hasFloor = _epochFloors.TryGetValue(key, out var floor);
            var minRequired = Math.Max(highestSeen, hasFloor ? floor : 0) + 1;
            if (incremented < minRequired)
            {
                // Redis lost epochs (restart/rollback): the bump must still land above everything cached before the revoke.
                var evalResult = await db.ScriptEvaluateAsync(
                    RollbackLuaScript,
                    [redisKey],
                    [(RedisValue)minRequired.ToString()]).ConfigureAwait(false);
                incremented = evalResult is not null && !evalResult.IsNull && long.TryParse(evalResult.ToString(), out var scriptVal)
                    ? scriptVal
                    : minRequired;
            }

            _highestSeenRedisEpoch.AddOrUpdate(key, incremented, (_, current) => Math.Max(current, incremented));
            if (hasFloor && incremented > floor)
            {
                _epochFloors.TryRemove(new KeyValuePair<string, long>(key, floor));
            }

            return true;
        }
        catch (Exception ex)
        {
            if (logFailureAsWarning)
            {
                _logger?.LogWarning(ex, "Policy epoch increment in Redis failed for {Table}; the bump stays pending and cached decisions for the table are re-evaluated until it succeeds (fail closed).", key);
            }
            else
            {
                _logger?.LogDebug(ex, "Retry of the pending policy epoch increment in Redis failed for {Table}.", key);
            }

            return false;
        }
    }

    private void LogRollbackWarning(string key, long redisEpoch, long highestSeen, long restored)
    {
        var now = _time.GetUtcNow();
        if (_lastRollbackWarningTime.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(30))
        {
            return;
        }

        bool shouldLog = false;
        _lastRollbackWarningTime.AddOrUpdate(
            key,
            _ => { shouldLog = true; return now; },
            (_, lastSeen) =>
            {
                if (now - lastSeen >= TimeSpan.FromSeconds(30))
                {
                    shouldLog = true;
                    return now;
                }
                shouldLog = false;
                return lastSeen;
            });

        if (shouldLog)
        {
            _logger?.LogWarning("Policy epoch rollback detected for {Table} (redis={RedisEpoch}, highestSeen={HighestSeen}); forcing epoch {Restored}.", key, redisEpoch, highestSeen, restored);
        }
    }

    public async Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default)
    {
        bool isDegraded = _multiplexer != null && !_multiplexer.IsConnected;
        var key = table.ToString().ToLowerInvariant();

        if (!isDegraded)
        {
            await FlushPendingRedisBumpsAsync(waitForLock: false).ConfigureAwait(false);
        }

        if (_pendingRedisBumps.ContainsKey(key))
        {
            // The local revoke is not confirmed in Redis yet: never confirm an entry for this table on this node.
            return false;
        }

        // A revoke whose Redis bump is outstanding is treated like the degraded mode for sensitive tables (fail closed).
        if ((isDegraded || !_pendingRedisBumps.IsEmpty) && _options.FailClosedOnSensitiveTables)
        {
            if (await _sensitivityLookup.IsSensitiveAsync(table, ct).ConfigureAwait(false))
            {
                return false;
            }
        }

        if (isDegraded)
        {
            if (_lastEpochRefresh.TryGetValue(key, out var lastRefresh))
            {
                if (_time.GetUtcNow() - lastRefresh > TimeSpan.FromSeconds(_options.DegradedMaxStalenessSeconds))
                {
                    return false;
                }
            }
        }

        var now = _time.GetUtcNow();
        var budgetMs = _options.LocalStalenessBudgetMilliseconds;
        bool isSensitive = await _sensitivityLookup.IsSensitiveAsync(table, ct).ConfigureAwait(false);

        if (!isSensitive && budgetMs > 0 && !_epochFloors.ContainsKey(key) && _microCache.TryGetValue(key, out var entry))
        {
            if ((now - entry.CachedAt).TotalMilliseconds <= budgetMs)
            {
                _highestSeenRedisEpoch.TryGetValue(key, out var highestSeen);
                if (entry.Epoch >= highestSeen)
                {
                    return entry.Epoch == cachedEpoch;
                }
            }
        }

        var (current, authoritative) = await GetCurrentEpochCoreAsync(table).ConfigureAwait(false);
        if (!authoritative && !isDegraded)
        {
            // INF-1: Redis is configured and reported as connected, but the epoch could not be read -> fail closed.
            return false;
        }

        if (authoritative && budgetMs > 0 && _microCache.Count < _options.MaxLocalEpochEntries)
        {
            _microCache[key] = new LocalEpochEntry(current, _time.GetUtcNow());
        }

        return current == cachedEpoch;
    }

    public async Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        EnsureSubscribed();
        var key = table.ToString().ToLowerInvariant();
        _microCache.TryRemove(key, out _);
        if (_multiplexer != null)
        {
            // Fail closed until Redis confirms the bump: peers trust the Redis epoch, so a lost INCR would otherwise keep
            // every cached allow decision valid until its TTL.
            RaiseEpochFloor(key);
            _pendingRedisBumps[key] = Interlocked.Increment(ref _pendingBumpGeneration);
        }

        _epochs.AddOrUpdate(key, 2, (_, current) => current + 1);
        _lastEpochRefresh[key] = _time.GetUtcNow();

        if (_multiplexer != null)
        {
            if (_multiplexer.IsConnected)
            {
                await FlushPendingRedisBumpsAsync(waitForLock: true).ConfigureAwait(false);
            }
            else
            {
                _logger?.LogWarning("Redis unavailable while invalidating the policy epoch for {Table}; the bump stays pending and is written on reconnect (fail closed).", key);
            }

            _ownInvalidationsInFlight.AddOrUpdate(key, 1, (_, count) => count + 1);
        }

        // Broadcast invalidation event
        try
        {
            await _eventBus.PublishAsync(_invalidationChannel, key, ct).ConfigureAwait(false);
        }
        catch
        {
            if (_multiplexer != null)
            {
                TryConsumeOwnInvalidation(key);
            }

            throw;
        }
    }
}
