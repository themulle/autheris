namespace Autheris.Infrastructure.Identity.Locks;

using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

public sealed class RedisLockPubSubChannel : IRedisLockPubSubChannel, IDisposable
{
    public const string ChannelName = "autheris:identity:locks";
    private static readonly RedisChannel RedisPubSubChannel = RedisChannel.Literal(ChannelName);

    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly ILogger<RedisLockPubSubChannel> _logger;
    private readonly ConcurrentDictionary<Guid, Action<LockPubSubMessage>> _localSubscribers = new();
    private readonly ISubscriber? _subscriber;
    private readonly Action<RedisChannel, RedisValue>? _redisHandler;
    private int _disposed;

    public RedisLockPubSubChannel(
        IConnectionMultiplexer? multiplexer = null,
        ILogger<RedisLockPubSubChannel>? logger = null)
    {
        _multiplexer = multiplexer;
        _logger = logger ?? NullLogger<RedisLockPubSubChannel>.Instance;

        if (_multiplexer != null)
        {
            _redisHandler = OnRedisMessageReceived;
            try
            {
                _subscriber = _multiplexer.GetSubscriber();
                _subscriber?.Subscribe(RedisPubSubChannel, _redisHandler, CommandFlags.FireAndForget);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to subscribe to Redis lock pub/sub channel '{Channel}'; using local fallback.", ChannelName);
            }
        }
    }

    public async Task PublishLockAsync(LockPubSubMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Notify local subscribers first
        NotifyLocal(message);

        // Publish to Redis if connected
        if (_multiplexer != null && _multiplexer.IsConnected && _subscriber != null)
        {
            try
            {
                var json = JsonSerializer.Serialize(message);
                await _subscriber.PublishAsync(RedisPubSubChannel, json, CommandFlags.FireAndForget).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish lock message for user '{UserKey}' to Redis channel '{Channel}'.", message.UserKey, ChannelName);
            }
        }
    }

    public IDisposable Subscribe(Action<LockPubSubMessage> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var id = Guid.NewGuid();
        _localSubscribers.TryAdd(id, handler);
        return new SubscriptionToken(() => _localSubscribers.TryRemove(id, out _));
    }

    private void OnRedisMessageReceived(RedisChannel channel, RedisValue payload)
    {
        if (payload.IsNullOrEmpty) return;

        try
        {
            var msg = JsonSerializer.Deserialize<LockPubSubMessage>(payload.ToString());
            if (msg != null)
            {
                NotifyLocal(msg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize lock pub/sub message.");
        }
    }

    private void NotifyLocal(LockPubSubMessage message)
    {
        foreach (var sub in _localSubscribers.Values)
        {
            try
            {
                sub(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing lock pub/sub subscriber handler.");
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _localSubscribers.Clear();
        if (_subscriber != null && _redisHandler != null)
        {
            try
            {
                _subscriber.Unsubscribe(RedisPubSubChannel, _redisHandler, CommandFlags.FireAndForget);
            }
            catch
            {
                // ignore on shutdown
            }
        }
    }

    private sealed class SubscriptionToken(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose()
        {
            Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
        }
    }
}
