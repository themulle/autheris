using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Autheris.Infrastructure.Cache;

public sealed class EpochValidationService : IEpochValidationService
{
    private sealed record LocalEpochEntry(long Epoch, DateTimeOffset CachedAt);

    private readonly ConcurrentDictionary<string, long> _epochs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastEpochRefresh = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LocalEpochEntry> _microCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly EpochValidationOptions _options;
    private readonly IEventBus _eventBus;
    private readonly string _invalidationChannel;
    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly string _redisPrefix;
    private readonly ConcurrentDictionary<string, long> _highestSeenRedisEpoch = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<EpochValidationService>? _logger;
    private readonly ITableSensitivityLookup _sensitivityLookup;

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
        ILogger<EpochValidationService>? logger = null)
    {
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
            };
        }

        var prefix = options?.Value?.Caching?.Redis?.InstanceName ?? "autheris:";
        _redisPrefix = prefix.EndsWith(':') ? prefix : prefix + ":";

        _eventBus.Subscribe<string>(_invalidationChannel, message =>
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                var key = message.ToLowerInvariant();
                _epochs.AddOrUpdate(key, 2, (_, current) => current + 1);
                _lastEpochRefresh[key] = DateTimeOffset.UtcNow;
                _microCache.TryRemove(key, out _);
            }
            return Task.CompletedTask;
        });
    }

    public async Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default)
        => (await GetCurrentEpochCoreAsync(table).ConfigureAwait(false)).Epoch;

    /// <summary>
    /// INF-1: <c>Authoritative</c> is false when Redis is configured but the read failed. The local epoch is then
    /// only a fallback and must not confirm a cached decision (another node may have revoked it meanwhile).
    /// </summary>
    private async Task<(long Epoch, bool Authoritative)> GetCurrentEpochCoreAsync(TableIdentifier table)
    {
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
                _lastEpochRefresh[key] = DateTimeOffset.UtcNow;
                return (rEpoch, true);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Policy epoch read from Redis failed for {Table}; cached decisions are re-evaluated (degraded).", key);
                return (_epochs.GetOrAdd(key, 1), false);
            }
        }

        var epoch = _epochs.GetOrAdd(key, 1);
        _lastEpochRefresh.TryAdd(key, DateTimeOffset.UtcNow);
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
                    if (DateTimeOffset.UtcNow - lastRefresh > TimeSpan.FromSeconds(_options.DegradedMaxStalenessSeconds))
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

        var now = DateTimeOffset.UtcNow;
        var pendingRedisChecks = new List<EpochCheck>();
        var budgetMs = _options.LocalStalenessBudgetMilliseconds;

        foreach (var check in checks)
        {
            var key = check.Table.ToString().ToLowerInvariant();

            // Sensitive tables NEVER use micro-cache (zero-tolerance revocation)
            if (!check.IsHighlySensitive && budgetMs > 0 && _microCache.TryGetValue(key, out var entry))
            {
                if ((now - entry.CachedAt).TotalMilliseconds <= budgetMs)
                {
                    _highestSeenRedisEpoch.TryGetValue(key, out var highestSeen);
                    if (entry.Epoch >= highestSeen)
                    {
                        results[check.Table] = entry.Epoch == check.CachedEpoch;
                        continue;
                    }
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

            if (_options.PipelinedMGetEnabled)
            {
                var redisKeys = pendingRedisChecks
                    .Select(c => (RedisKey)$"{_redisPrefix}epoch:{c.Table.ToString().ToLowerInvariant()}")
                    .ToArray();

                var values = await db.StringGetAsync(redisKeys).ConfigureAwait(false);

                for (int i = 0; i < pendingRedisChecks.Count; i++)
                {
                    var check = pendingRedisChecks[i];
                    var key = check.Table.ToString().ToLowerInvariant();
                    var redisKey = redisKeys[i];
                    var val = values[i];

                    long authoritativeEpoch;
                    _highestSeenRedisEpoch.TryGetValue(key, out var highestSeen);

                    long parsed = val.HasValue && long.TryParse(val.ToString(), out var p) ? p : 0;
                    if (parsed <= 0 || parsed < highestSeen)
                    {
                        var minRequired = highestSeen > 0 ? highestSeen + 1 : 1;
                        var evalResult = await db.ScriptEvaluateAsync(
                            RollbackLuaScript,
                            [redisKey],
                            [(RedisValue)minRequired.ToString()]).ConfigureAwait(false);

                        if (evalResult is not null && !evalResult.IsNull && long.TryParse(evalResult.ToString(), out var scriptVal))
                        {
                            authoritativeEpoch = scriptVal;
                        }
                        else
                        {
                            authoritativeEpoch = minRequired;
                        }

                        _highestSeenRedisEpoch.AddOrUpdate(key, authoritativeEpoch, (_, old) => Math.Max(old, authoritativeEpoch));
                    }
                    else
                    {
                        authoritativeEpoch = parsed;
                        _highestSeenRedisEpoch.AddOrUpdate(key, authoritativeEpoch, (_, old) => Math.Max(old, authoritativeEpoch));
                    }

                    _epochs[key] = authoritativeEpoch;
                    _lastEpochRefresh[key] = DateTimeOffset.UtcNow;

                    if (budgetMs > 0 && _microCache.Count < _options.MaxLocalEpochEntries)
                    {
                        _microCache[key] = new LocalEpochEntry(authoritativeEpoch, DateTimeOffset.UtcNow);
                    }

                    results[check.Table] = authoritativeEpoch == check.CachedEpoch;
                }
            }
            else
            {
                foreach (var check in pendingRedisChecks)
                {
                    var key = check.Table.ToString().ToLowerInvariant();
                    var redisKey = (RedisKey)$"{_redisPrefix}epoch:{key}";
                    var val = await db.StringGetAsync(redisKey).ConfigureAwait(false);

                    long authoritativeEpoch;
                    _highestSeenRedisEpoch.TryGetValue(key, out var highestSeen);

                    long parsed = val.HasValue && long.TryParse(val.ToString(), out var p) ? p : 0;
                    if (parsed <= 0 || parsed < highestSeen)
                    {
                        var minRequired = highestSeen > 0 ? highestSeen + 1 : 1;
                        var evalResult = await db.ScriptEvaluateAsync(
                            RollbackLuaScript,
                            [redisKey],
                            [(RedisValue)minRequired.ToString()]).ConfigureAwait(false);

                        if (evalResult is not null && !evalResult.IsNull && long.TryParse(evalResult.ToString(), out var scriptVal))
                        {
                            authoritativeEpoch = scriptVal;
                        }
                        else
                        {
                            authoritativeEpoch = minRequired;
                        }

                        _highestSeenRedisEpoch.AddOrUpdate(key, authoritativeEpoch, (_, old) => Math.Max(old, authoritativeEpoch));
                    }
                    else
                    {
                        authoritativeEpoch = parsed;
                        _highestSeenRedisEpoch.AddOrUpdate(key, authoritativeEpoch, (_, old) => Math.Max(old, authoritativeEpoch));
                    }

                    _epochs[key] = authoritativeEpoch;
                    _lastEpochRefresh[key] = DateTimeOffset.UtcNow;

                    if (budgetMs > 0 && _microCache.Count < _options.MaxLocalEpochEntries)
                    {
                        _microCache[key] = new LocalEpochEntry(authoritativeEpoch, DateTimeOffset.UtcNow);
                    }

                    results[check.Table] = authoritativeEpoch == check.CachedEpoch;
                }
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
                if (DateTimeOffset.UtcNow - lastRefresh > TimeSpan.FromSeconds(_options.DegradedMaxStalenessSeconds))
                {
                    return false;
                }
            }
        }

        var now = DateTimeOffset.UtcNow;
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
            _microCache[key] = new LocalEpochEntry(current, DateTimeOffset.UtcNow);
        }

        return current == cachedEpoch;
    }

    public async Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var key = table.ToString().ToLowerInvariant();
        _microCache.TryRemove(key, out _);
        _epochs.AddOrUpdate(key, 2, (_, current) => current + 1);
        _lastEpochRefresh[key] = DateTimeOffset.UtcNow;

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
