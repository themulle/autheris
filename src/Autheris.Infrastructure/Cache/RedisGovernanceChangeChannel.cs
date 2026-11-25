using System.Collections.Concurrent;
using System.Text.Json;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Autheris.Infrastructure.Cache;

/// <summary>
/// Broadcasts governance change notifications locally and over Redis pub/sub,
/// failing closed to full cache invalidation when payload deserialization or subscriber handling fails.
/// </summary>
public sealed class RedisGovernanceChangeChannel : IGovernanceChangeNotifier, IDisposable
{
    public const string ChannelName = "autheris:governance:changes";
    private static readonly RedisChannel RedisPubSubChannel = RedisChannel.Literal(ChannelName);

    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly TimeProvider _time;
    private readonly ILogger<RedisGovernanceChangeChannel> _logger;
    private readonly ConcurrentDictionary<Guid, Action<GovernanceChangeEvent>> _localSubscribers = new();
    private readonly ISubscriber? _subscriber;
    private readonly Action<RedisChannel, RedisValue>? _redisHandler;
    private readonly EventHandler<ConnectionFailedEventArgs>? _connectionRestoredHandler;
    private int _disposed;

    public RedisGovernanceChangeChannel(
        IConnectionMultiplexer? multiplexer = null,
        TimeProvider? timeProvider = null,
        ILogger<RedisGovernanceChangeChannel>? logger = null)
    {
        _multiplexer = multiplexer;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<RedisGovernanceChangeChannel>.Instance;

        if (_multiplexer is not null)
        {
            _redisHandler = OnRedisMessageReceived;
            _connectionRestoredHandler = OnConnectionRestored;
            try
            {
                _subscriber = _multiplexer.GetSubscriber();
                _subscriber?.Subscribe(RedisPubSubChannel, _redisHandler, CommandFlags.FireAndForget);
                _multiplexer.ConnectionRestored += _connectionRestoredHandler;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to subscribe to Redis governance change channel '{Channel}'; using local invalidation only.", ChannelName);
            }
        }
    }

    public async Task NotifyAsync(GovernanceChangeEvent change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ct.ThrowIfCancellationRequested();

        // Invalidate local in-process caches first before cross-node broadcast.
        NotifyLocal(change);

        if (_multiplexer is not null && _multiplexer.IsConnected && _subscriber is not null)
        {
            try
            {
                var json = JsonSerializer.Serialize(change);
                await _subscriber.PublishAsync(RedisPubSubChannel, json, CommandFlags.FireAndForget).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to publish governance change for tenant '{TenantId}' to Redis channel '{Channel}'.",
                    LogSanitizer.Sanitize(change.TenantId),
                    ChannelName);
            }
        }
    }

    public IDisposable Subscribe(Action<GovernanceChangeEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var id = Guid.NewGuid();
        _localSubscribers.TryAdd(id, handler);
        return new SubscriptionToken(() => _localSubscribers.TryRemove(id, out _));
    }

    private void OnRedisMessageReceived(RedisChannel channel, RedisValue payload)
    {
        if (payload.IsNullOrEmpty)
        {
            _logger.LogWarning("Received empty Redis governance change payload on '{Channel}'; forcing full cache invalidation.", ChannelName);
            NotifyLocal(GovernanceChangeEvent.All(_time.GetUtcNow()));
            return;
        }

        try
        {
            var msg = JsonSerializer.Deserialize<GovernanceChangeEvent>(payload.ToString());
            if (msg is null || msg.Epoch < 0 || (!msg.IsFullInvalidation && !TenantId.TryParse(msg.TenantId, out _)))
            {
                _logger.LogWarning("Received invalid Redis governance change payload on '{Channel}'; forcing full cache invalidation.", ChannelName);
                NotifyLocal(GovernanceChangeEvent.All(_time.GetUtcNow()));
                return;
            }

            NotifyLocal(msg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize Redis governance change payload on '{Channel}'; forcing full cache invalidation.", ChannelName);
            NotifyLocal(GovernanceChangeEvent.All(_time.GetUtcNow()));
        }
    }

    private void OnConnectionRestored(object? sender, ConnectionFailedEventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _logger.LogWarning(e?.Exception, "Redis governance change connection restored ({FailureType}); flushing local governance caches.", e?.FailureType);
        NotifyLocal(GovernanceChangeEvent.All(_time.GetUtcNow()));
    }

    private void NotifyLocal(GovernanceChangeEvent change)
    {
        foreach (var subscriber in _localSubscribers.Values)
        {
            try
            {
                subscriber(change);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Governance change subscriber threw while handling notification; forcing full invalidation.");
                if (!change.IsFullInvalidation)
                {
                    try
                    {
                        subscriber(GovernanceChangeEvent.All(_time.GetUtcNow()));
                    }
                    catch (Exception fallbackEx)
                    {
                        _logger.LogError(fallbackEx, "Governance change subscriber failed during full invalidation fallback.");
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _localSubscribers.Clear();
        if (_multiplexer is not null && _connectionRestoredHandler is not null)
        {
            try
            {
                _multiplexer.ConnectionRestored -= _connectionRestoredHandler;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to detach ConnectionRestored handler from Redis multiplexer.");
            }
        }

        if (_subscriber is not null && _redisHandler is not null)
        {
            try
            {
                _subscriber.Unsubscribe(RedisPubSubChannel, _redisHandler, CommandFlags.FireAndForget);
            }
            catch
            {
                // Ignore unsubscribe errors during shutdown.
            }
        }
    }

    private sealed class SubscriptionToken(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
