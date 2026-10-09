using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Autheris.Infrastructure.Messaging;

public sealed class RedisEventBus : IEventBus, IDisposable
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly ILogger<RedisEventBus> _logger;
    private readonly string _prefix;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly List<IDisposable> _subscriptions = [];
    private readonly object _lock = new();
    private bool _disposed;

    public event Action? ConnectionRestored;

    public RedisEventBus(
        IConnectionMultiplexer multiplexer,
        IOptions<GatewayOptions> options,
        ILogger<RedisEventBus> logger)
    {
        _multiplexer = multiplexer;
        _logger = logger;
        _prefix = options.Value.Caching.Redis.InstanceName;
        if (!_prefix.EndsWith(':'))
        {
            _prefix += ":";
        }

        _multiplexer.ConnectionRestored += (s, e) =>
        {
            _logger.LogInformation("Redis event bus connection restored. Firing ConnectionRestored callbacks.");
            ConnectionRestored?.Invoke();
        };
    }

    public async Task PublishAsync<T>(string channel, T message, CancellationToken ct = default)
    {
        if (message == null) return;
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var sub = _multiplexer.GetSubscriber();
            var fullChannel = $"{_prefix}{channel}";
            var json = JsonSerializer.Serialize(message, _jsonOptions);
            await sub.PublishAsync(RedisChannel.Literal(fullChannel), json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish message of type {Type} to Redis channel {Channel}", typeof(T).Name, channel);
            throw;
        }
    }

    public async Task<long> IncrementCounterAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var db = _multiplexer.GetDatabase();
            var fullKey = $"{_prefix}{key}";
            return await db.StringIncrementAsync(fullKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to increment Redis counter {Key}", key);
            throw;
        }
    }

    public async Task<long> GetCounterAsync(string key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var db = _multiplexer.GetDatabase();
            var fullKey = $"{_prefix}{key}";
            var val = await db.StringGetAsync(fullKey).ConfigureAwait(false);
            if (val.IsNullOrEmpty)
            {
                return 0L;
            }
            return (long)val;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Redis counter {Key}", key);
            throw;
        }
    }

    public IDisposable Subscribe<T>(string channel, Func<T, Task> handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sub = _multiplexer.GetSubscriber();
        var fullChannel = $"{_prefix}{channel}";

        void OnMessage(RedisChannel channelName, RedisValue value)
        {
            if (value.IsNullOrEmpty) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    var msg = JsonSerializer.Deserialize<T>(value.ToString(), _jsonOptions);
                    if (msg != null)
                    {
                        await handler(msg).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing Redis subscription event for channel {Channel}", channel);
                }
            });
        }

        var redisChannel = RedisChannel.Literal(fullChannel);
        sub.Subscribe(redisChannel, OnMessage);

        var unsubscriber = new RedisSubscriptionUnsubscriber(sub, redisChannel, OnMessage);
        lock (_lock)
        {
            _subscriptions.Add(unsubscriber);
        }
        return unsubscriber;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            foreach (var s in _subscriptions)
            {
                s.Dispose();
            }
            _subscriptions.Clear();
        }
    }

    private sealed class RedisSubscriptionUnsubscriber(
        ISubscriber subscriber,
        RedisChannel channel,
        Action<RedisChannel, RedisValue> handler) : IDisposable
    {
        private bool _unsubscribed;

        public void Dispose()
        {
            if (_unsubscribed) return;
            _unsubscribed = true;
            try
            {
                subscriber.Unsubscribe(channel, handler);
            }
            catch
            {
                // Ignore unsubscribe errors during teardown
            }
        }
    }
}
