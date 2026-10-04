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

    private string BuildKey(string key) => _prefix + key;

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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read key {Key} from Redis state store.", key);
            return default;
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set key {Key} in Redis state store.", key);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete key {Key} from Redis state store.", key);
            return false;
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish cluster event to channel {Channel}.", channel);
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
                    _ = handler(payload).AsTask();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing cluster event from channel {Channel}.", channel);
            }
        });

        return new RedisSubscriptionLease(sub, redisChannel);
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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to acquire distributed lock for resource {Resource}.", resourceKey);
            return null;
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
