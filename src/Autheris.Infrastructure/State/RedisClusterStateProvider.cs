namespace Autheris.Infrastructure.State;

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

/// <summary>
/// K-K14: Enterprise Redis cluster state provider.
/// Implements distributed state, distributed locking, and pub/sub cache invalidation.
/// </summary>
public sealed class RedisClusterStateProvider : IDistributedClusterStateProvider
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly ILogger<RedisClusterStateProvider> _logger;
    private readonly string _prefix;

    public RedisClusterStateProvider(
        IConnectionMultiplexer multiplexer,
        IOptions<GatewayOptions> options,
        ILogger<RedisClusterStateProvider> logger)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(options);

        var instance = options.Value.Caching.Redis.InstanceName;
        if (string.IsNullOrWhiteSpace(instance))
        {
            instance = "Autheris:";
        }
        else if (!instance.EndsWith(':'))
        {
            instance += ":";
        }

        _prefix = instance + "state:";
    }

    private static readonly Prometheus.Counter StateFailures = Prometheus.Metrics.CreateCounter(
        "autheris_cluster_state_failure_total", "Number of infrastructure failures of the Redis cluster state store",
        new Prometheus.CounterConfiguration { LabelNames = ["operation"] });

    private static readonly Prometheus.Counter SubscriberHandlerFailures = Prometheus.Metrics.CreateCounter(
        "autheris_cluster_state_handler_failure_total", "Number of failed cluster event handler invocations");

    private string BuildKey(string key) => _prefix + key;

    /// <summary>
    /// Infrastructure failures (connection, timeout, Redis server errors) are surfaced as
    /// <see cref="ClusterStateUnavailableException"/>; anything else (programming errors, cancellation) is not masked.
    /// </summary>
    private static bool IsInfrastructureFailure(Exception ex) =>
        ex is RedisException or TimeoutException or System.IO.IOException or System.Net.Sockets.SocketException or ObjectDisposedException;

    private ClusterStateUnavailableException Unavailable(string operation, string key, Exception ex)
    {
        StateFailures.WithLabels(operation).Inc();
        _logger.LogWarning(ex, "Redis cluster state operation {Operation} failed for key {Key}.", operation, key);
        return new ClusterStateUnavailableException(operation, key, ex);
    }

    public async ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        try
        {
            var db = _multiplexer.GetDatabase();
            var val = await db.StringGetAsync((RedisKey)BuildKey(key)).ConfigureAwait(false);
            if (val.IsNullOrEmpty)
            {
                return default;
            }

            return JsonSerializer.Deserialize<T>(val.ToString());
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex))
        {
            throw Unavailable("get", key, ex);
        }
    }

    public async ValueTask SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            var db = _multiplexer.GetDatabase();
            var serialized = JsonSerializer.Serialize(value);
            await db.StringSetAsync((RedisKey)BuildKey(key), (RedisValue)serialized, ttl).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex))
        {
            throw Unavailable("set", key, ex);
        }
    }

    public async ValueTask<bool> RemoveAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        try
        {
            var db = _multiplexer.GetDatabase();
            return await db.KeyDeleteAsync((RedisKey)BuildKey(key)).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex))
        {
            throw Unavailable("remove", key, ex);
        }
    }

    public async ValueTask<long?> IncrementAsync(string key, long delta, TimeSpan ttl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        try
        {
            var db = _multiplexer.GetDatabase();
            var redisKey = (RedisKey)BuildKey(key);
            var value = await db.StringIncrementAsync(redisKey, delta).ConfigureAwait(false);
            if (value == delta && ttl > TimeSpan.Zero && ttl < TimeSpan.MaxValue)
            {
                // Counter was just created: bound its lifetime.
                await db.KeyExpireAsync(redisKey, ttl).ConfigureAwait(false);
            }

            return value;
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex))
        {
            // Documented contract: null means "store unavailable" for counters (never "zero").
            StateFailures.WithLabels("increment").Inc();
            _logger.LogWarning(ex, "Failed to increment counter {Key} in Redis state store.", key);
            return null;
        }
    }

    private const string TryConsumeBudgetScript = @"
local current = redis.call('GET', KEYS[1])
if not current then
    current = 0
else
    current = tonumber(current)
end
local cost = tonumber(ARGV[1])
local limit = tonumber(ARGV[2])
local ttl_seconds = tonumber(ARGV[3])

if current + cost <= limit then
    local new_val = redis.call('INCRBY', KEYS[1], cost)
    if current == 0 and ttl_seconds > 0 then
        redis.call('EXPIRE', KEYS[1], ttl_seconds)
    end
    return {1, new_val}
else
    return {0, current}
end
";

    public async ValueTask<(BudgetConsumeOutcome Outcome, long ConsumedAfter)> TryConsumeBudgetAsync(
        string key, long cost, long limit, TimeSpan ttl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        try
        {
            var db = _multiplexer.GetDatabase();
            var redisKey = (RedisKey)BuildKey(key);
            var ttlSeconds = (ttl > TimeSpan.Zero && ttl < TimeSpan.MaxValue) ? (long)ttl.TotalSeconds : 0L;

            var res = await db.ScriptEvaluateAsync(
                TryConsumeBudgetScript,
                [redisKey],
                [cost, limit, ttlSeconds]).ConfigureAwait(false);

            var arr = (RedisResult[]?)res;
            if (arr is { Length: 2 })
            {
                var success = (long)arr[0] == 1;
                var total = (long)arr[1];
                return (success ? BudgetConsumeOutcome.Consumed : BudgetConsumeOutcome.Exhausted, total);
            }

            return (BudgetConsumeOutcome.StoreUnavailable, 0);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex))
        {
            StateFailures.WithLabels("consume_budget").Inc();
            _logger.LogError(ex, "Failed to execute atomic budget consume script for key {Key} in Redis state store.", key);
            return (BudgetConsumeOutcome.StoreUnavailable, 0);
        }
    }

    public async ValueTask PublishEventAsync<T>(string channel, T payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        try
        {
            var sub = _multiplexer.GetSubscriber();
            var serialized = JsonSerializer.Serialize(payload);
            await sub.PublishAsync(RedisChannel.Literal(BuildKey(channel)), (RedisValue)serialized).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex))
        {
            throw Unavailable("publish", channel, ex);
        }
    }

    public IAsyncDisposable SubscribeAsync<T>(string channel, Func<T, ValueTask> handler, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);

        var redisChannel = RedisChannel.Literal(BuildKey(channel));
        var sub = _multiplexer.GetSubscriber();

        sub.Subscribe(redisChannel, (ch, msg) =>
        {
            if (msg.IsNullOrEmpty) return;
            try
            {
                var payload = JsonSerializer.Deserialize<T>(msg.ToString());
                if (payload != null)
                {
                    _ = InvokeHandlerAsync(handler, payload, channel);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing cluster event from channel {Channel}.", channel);
            }
        });

        return new RedisSubscriptionLease(sub, redisChannel);
    }

    private async Task InvokeHandlerAsync<T>(Func<T, ValueTask> handler, T payload, string channel)
    {
        try
        {
            await handler(payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SubscriberHandlerFailures.Inc();
            _logger.LogError(ex, "Cluster event handler failed for channel {Channel}.", channel);
        }
    }

    public async ValueTask<IAsyncDisposable?> TryAcquireLockAsync(string resourceKey, TimeSpan expiry, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        try
        {
            var db = _multiplexer.GetDatabase();
            var lockKey = (RedisKey)(_prefix + "lock:" + resourceKey);
            var lockValue = (RedisValue)Guid.NewGuid().ToString("N");

            var acquired = await db.LockTakeAsync(lockKey, lockValue, expiry).ConfigureAwait(false);
            if (!acquired)
            {
                return null;
            }

            return new RedisLockLease(db, lockKey, lockValue);
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex))
        {
            // null is reserved for "lock busy"; an unreachable store must not look like a busy lock.
            throw Unavailable("lock", resourceKey, ex);
        }
    }

    private sealed class RedisSubscriptionLease : IAsyncDisposable
    {
        private readonly ISubscriber _subscriber;
        private readonly RedisChannel _channel;
        public RedisSubscriptionLease(ISubscriber subscriber, RedisChannel channel)
        {
            _subscriber = subscriber;
            _channel = channel;
        }

        public ValueTask DisposeAsync()
        {
            _subscriber.Unsubscribe(_channel);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RedisLockLease : IAsyncDisposable
    {
        private readonly IDatabase _db;
        private readonly RedisKey _key;
        private readonly RedisValue _value;
        private int _released;

        public RedisLockLease(IDatabase db, RedisKey key, RedisValue value)
        {
            _db = db;
            _key = key;
            _value = value;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                await _db.LockReleaseAsync(_key, _value).ConfigureAwait(false);
            }
        }
    }
}
