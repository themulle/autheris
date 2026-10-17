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
    private readonly ILogger<EpochValidationService>? _logger;
    private readonly ITableSensitivityLookup _sensitivityLookup;
    private readonly object _subscriptionLock = new();
    private IDisposable? _invalidationSubscription;

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
            _multiplexer.ConnectionRestored += (sender, args) =>
            {
                _logger?.LogInformation("Redis connection restored. Evicting local L1 epoch cache to synchronize with cluster (POL-9).");
                _epochs.Clear();
                _lastEpochRefresh.Clear();
                _microCache.Clear();
                EnsureSubscribed();
            };
        }

        var prefix = options?.Value?.Caching?.Redis?.InstanceName ?? "autheris:";
        _redisPrefix = prefix.EndsWith(':') ? prefix : prefix + ":";
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
                var db = _multiplexer.GetDatabase();
                var redisKey = $"{_redisPrefix}epoch:{key}";
                var redisVal = await db.StringGetAsync(redisKey).ConfigureAwait(false);
                long rEpoch = redisVal.HasValue && long.TryParse(redisVal.ToString(), out var parsed) ? parsed : 0;
                _highestSeenRedisEpoch.TryGetValue(key, out var highestSeen);

                if (rEpoch <= 0 || rEpoch < highestSeen)
                {
                    // Missing or rolled-back epoch: never fall back below anything this node has already seen.
                    var restored = highestSeen > 0 ? highestSeen + 1 : 1;
                    _logger?.LogWarning("Policy epoch rollback detected for {Table} (redis={RedisEpoch}, highestSeen={HighestSeen}); forcing epoch {Restored}.", key, rEpoch, highestSeen, restored);
                    var evalResult = await db.ScriptEvaluateAsync(
                        RollbackLuaScript,
                        [(RedisKey)redisKey],
                        [(RedisValue)restored.ToString()]).ConfigureAwait(false);

                    rEpoch = evalResult is not null && !evalResult.IsNull && long.TryParse(evalResult.ToString(), out var scriptVal)
                        ? scriptVal
                        : restored;
                }

                _highestSeenRedisEpoch.AddOrUpdate(key, rEpoch, (_, current) => Math.Max(current, rEpoch));
                _epochs[key] = rEpoch;
                _lastEpochRefresh[key] = _time.GetUtcNow();
                return (rEpoch, true);
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

        var now = _time.GetUtcNow();
        var pendingRedisChecks = new List<EpochCheck>();
        var budgetMs = _options.LocalStalenessBudgetMilliseconds;

        foreach (var check in checks)
        {
            var key = check.Table.ToString().ToLowerInvariant();

            // SEC-SC-05: the staleness budget applies to non-sensitive tables of Autheris-enforced sources only. Sensitive
            // tables (flagged by the caller or by the metadata lookup, which fails closed) and delegated sources always read Redis.
            if (!check.IsHighlySensitive && !check.IsDelegatedSource && budgetMs > 0 && _microCache.TryGetValue(key, out var entry) &&
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
        long parsed = val.HasValue && long.TryParse(val.ToString(), out var p) ? p : 0;
        long authoritativeEpoch;
        if (parsed <= 0 || parsed < highestSeen)
        {
            var minRequired = highestSeen > 0 ? highestSeen + 1 : 1;
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
        return authoritativeEpoch;
    }

    public async Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default)
    {
        bool isDegraded = _multiplexer != null && !_multiplexer.IsConnected;
        var key = table.ToString().ToLowerInvariant();

        if (isDegraded)
        {
            if (_options.FailClosedOnSensitiveTables)
            {
                if (await _sensitivityLookup.IsSensitiveAsync(table, ct).ConfigureAwait(false))
                {
                    return false;
                }
            }

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

        if (!isSensitive && budgetMs > 0 && _microCache.TryGetValue(key, out var entry))
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
        _epochs.AddOrUpdate(key, 2, (_, current) => current + 1);
        _lastEpochRefresh[key] = _time.GetUtcNow();

        if (_multiplexer != null && _multiplexer.IsConnected)
        {
            try
            {
                var db = _multiplexer.GetDatabase();
                var incremented = await db.StringIncrementAsync($"{_redisPrefix}epoch:{key}").ConfigureAwait(false);
                _highestSeenRedisEpoch.AddOrUpdate(key, incremented, (_, current) => Math.Max(current, incremented));
            }
            catch (Exception ex)
            {
                // Fallback to local and event bus
                _logger?.LogWarning(ex, "Policy epoch increment in Redis failed for {Table}; relying on local epoch and event bus.", key);
            }
        }

        // Broadcast invalidation event
        await _eventBus.PublishAsync(_invalidationChannel, key, ct).ConfigureAwait(false);
    }
}
