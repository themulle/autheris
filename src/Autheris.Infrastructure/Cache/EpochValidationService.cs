using System.Collections.Concurrent;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Infrastructure.Cache;

public sealed class EpochValidationService : IEpochValidationService
{
    private readonly ConcurrentDictionary<string, long> _epochs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastEpochRefresh = new(StringComparer.OrdinalIgnoreCase);
    private readonly EpochValidationOptions _options;
    private readonly IEventBus _eventBus;
    private readonly string _invalidationChannel;
    private readonly StackExchange.Redis.IConnectionMultiplexer? _multiplexer;
    private readonly string _redisPrefix;
    private readonly ConcurrentDictionary<string, long> _highestSeenRedisEpoch = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<EpochValidationService>? _logger;

    // SEC H-01: Epoch values read from Redis/Garnet are not trusted blindly. Each node remembers the highest
    // epoch it has observed per table; a lower value (Redis restart without persistence, or a tampered key that
    // tries to re-validate an older, still-signed L2 entry) is treated as a rollback and forces a fresh epoch
    // above the highest observed one. Residual risk: a node that never observed the higher epoch cannot detect
    // the rollback, therefore Redis/Garnet must additionally be protected by authentication (see GarnetServerManager).
    public EpochValidationService(
        IOptions<GatewayOptions>? options = null,
        IEventBus? eventBus = null,
        StackExchange.Redis.IConnectionMultiplexer? multiplexer = null,
        ILogger<EpochValidationService>? logger = null)
    {
        _logger = logger;
        _options = options?.Value?.Caching?.EpochValidation ?? new EpochValidationOptions();
        _invalidationChannel = options?.Value?.Caching?.Redis?.InvalidationChannel ?? "consent:invalidations";
        _eventBus = eventBus ?? new Messaging.InProcessChannelEventBus();
        _multiplexer = multiplexer;

        var prefix = options?.Value?.Caching?.Redis?.InstanceName ?? "autheris:";
        _redisPrefix = prefix.EndsWith(':') ? prefix : prefix + ":";

        _eventBus.Subscribe<string>(_invalidationChannel, message =>
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                var key = message.ToLowerInvariant();
                _epochs.AddOrUpdate(key, 2, (_, current) => current + 1);
                _lastEpochRefresh[key] = DateTimeOffset.UtcNow;
            }
            return Task.CompletedTask;
        });
    }

    public async Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default)
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
                    if (highestSeen > 0)
                    {
                        _logger?.LogWarning("Policy epoch rollback detected for {Table} (redis={RedisEpoch}, highestSeen={HighestSeen}); forcing epoch {Restored}.", key, rEpoch, highestSeen, restored);
                        await db.StringSetAsync(redisKey, restored).ConfigureAwait(false);
                        rEpoch = restored;
                    }
                    else
                    {
                        // Atomic INCR creates a missing key with 1 and never overwrites a concurrently created epoch.
                        rEpoch = await db.StringIncrementAsync(redisKey).ConfigureAwait(false);
                    }
                }

                _highestSeenRedisEpoch.AddOrUpdate(key, rEpoch, (_, current) => Math.Max(current, rEpoch));
                _epochs[key] = rEpoch;
                _lastEpochRefresh[key] = DateTimeOffset.UtcNow;
                return rEpoch;
            }
            catch
            {
                // Fall back to local degraded evaluation
            }
        }

        var epoch = _epochs.GetOrAdd(key, 1);
        _lastEpochRefresh.TryAdd(key, DateTimeOffset.UtcNow);
        return epoch;
    }

    public async Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(
        IEnumerable<TableIdentifier> tables,
        CancellationToken ct = default)
    {
        var result = new Dictionary<TableIdentifier, long>();
        foreach (var t in tables)
        {
            result[t] = await GetCurrentEpochAsync(t, ct).ConfigureAwait(false);
        }
        return result;
    }

    public async Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default)
    {
        var key = table.ToString().ToLowerInvariant();

        // Check degraded / multi-node partition state:
        bool isDegraded = _multiplexer != null && !_multiplexer.IsConnected;
        if (isDegraded)
        {
            // SEC-EPOCH-01: In degraded state without Redis cluster coordination,
            // fail-closed on sensitive tables to prevent stale consent bypasses.
            if (_options.FailClosedOnSensitiveTables)
            {
                var isSensitiveTable = key.Contains("sensitive") ||
                                       key.Contains("employee") ||
                                       key.Contains("hr") ||
                                       key.Contains("patient") ||
                                       key.Contains("salary");
                if (isSensitiveTable)
                {
                    return false;
                }
            }

            // SEC-EPOCH-02: Enforce DegradedMaxStalenessSeconds
            if (_lastEpochRefresh.TryGetValue(key, out var lastRefresh))
            {
                if (DateTimeOffset.UtcNow - lastRefresh > TimeSpan.FromSeconds(_options.DegradedMaxStalenessSeconds))
                {
                    return false; // Stale epoch beyond degraded threshold -> fail closed
                }
            }
        }

        var current = await GetCurrentEpochAsync(table, ct).ConfigureAwait(false);
        return current == cachedEpoch;
    }

    public async Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var key = table.ToString().ToLowerInvariant();
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
            catch
            {
                // Fallback to local and event bus
            }
        }

        // Broadcast invalidation event
        await _eventBus.PublishAsync(_invalidationChannel, key, ct).ConfigureAwait(false);
    }
}
