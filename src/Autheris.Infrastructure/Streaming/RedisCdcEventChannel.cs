namespace Autheris.Infrastructure.Streaming;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

/// <summary>
/// WP-ARC-A-13: Cluster-wide CDC event channel backed by Redis Pub/Sub (<c>autheris:cdc:events</c>)
/// with a local <see cref="InMemoryCdcEventChannel"/> for in-process fan-out and history.
/// Uses a per-node instance identifier in the pub/sub envelope so events published on this node
/// are delivered locally once and are not double-delivered when echoed back from Redis.
/// </summary>
public sealed class RedisCdcEventChannel : ICdcEventChannel, IAsyncDisposable, IDisposable
{
    public const string PubSubChannelName = "autheris:cdc:events";
    private static readonly RedisChannel PubSubChannel = RedisChannel.Literal(PubSubChannelName);

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly InMemoryCdcEventChannel _inner;
    private readonly ILogger<RedisCdcEventChannel> _logger;
    private readonly string _instanceId;
    private readonly ISubscriber? _subscriber;
    private readonly Action<RedisChannel, RedisValue> _redisHandler;
    private int _disposed;

    public RedisCdcEventChannel(
        IConnectionMultiplexer multiplexer,
        InMemoryCdcEventChannel inner,
        ILogger<RedisCdcEventChannel> logger,
        string? instanceId = null)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _instanceId = string.IsNullOrWhiteSpace(instanceId) ? Guid.NewGuid().ToString("N") : instanceId;
        _redisHandler = OnRedisMessageReceived;

        try
        {
            _subscriber = _multiplexer.GetSubscriber();
            _subscriber?.Subscribe(PubSubChannel, _redisHandler, CommandFlags.FireAndForget);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe to Redis CDC pub/sub channel '{Channel}'; falling back to local CDC channel.", PubSubChannelName);
        }
    }

    public string InstanceId => _instanceId;

    public async ValueTask PublishAsync(CdcEvent cdcEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cdcEvent);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Always deliver to local subscribers immediately.
        await _inner.PublishAsync(cdcEvent, ct).ConfigureAwait(false);

        if (!_multiplexer.IsConnected)
        {
            _logger.LogDebug("Redis multiplexer is disconnected; CDC event '{EventId}' published locally only.", cdcEvent.EventId);
            return;
        }

        try
        {
            var subscriber = _subscriber ?? _multiplexer.GetSubscriber();
            if (subscriber is null)
            {
                return;
            }

            var envelope = new RedisCdcEnvelope(_instanceId, cdcEvent);
            var payload = JsonSerializer.Serialize(envelope);
            await subscriber.PublishAsync(PubSubChannel, payload, CommandFlags.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish CDC event '{EventId}' to Redis channel '{Channel}'; local subscribers already received the event.", cdcEvent.EventId, PubSubChannelName);
        }
    }

    public IAsyncEnumerable<CdcEvent> SubscribeAsync(string topic, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _inner.SubscribeAsync(topic, ct);
    }

    private void OnRedisMessageReceived(RedisChannel channel, RedisValue message)
    {
        if (Volatile.Read(ref _disposed) != 0 || message.IsNullOrEmpty)
        {
            return;
        }

        try
        {
            var json = (string?)message;
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var envelope = JsonSerializer.Deserialize<RedisCdcEnvelope>(json);
            if (envelope?.Event is null)
            {
                return;
            }

            // Ignore self-published events because they were already dispatched to _inner on PublishAsync.
            if (string.Equals(envelope.InstanceId, _instanceId, StringComparison.Ordinal))
            {
                return;
            }

            var normalized = NormalizeDeserializedEvent(envelope.Event);
            _ = DispatchRemoteEventAsync(normalized);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process incoming CDC event from Redis channel '{Channel}'.", PubSubChannelName);
        }
    }

    private async Task DispatchRemoteEventAsync(CdcEvent cdcEvent)
    {
        try
        {
            await _inner.PublishAsync(cdcEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to dispatch remote CDC event '{EventId}' to local channel.", cdcEvent.EventId);
        }
    }

    private static CdcEvent NormalizeDeserializedEvent(CdcEvent ev)
    {
        return ev with
        {
            Before = NormalizeDictionary(ev.Before),
            After = NormalizeDictionary(ev.After)
        };
    }

    private static IReadOnlyDictionary<string, object?>? NormalizeDictionary(IReadOnlyDictionary<string, object?>? source)
    {
        if (source is null)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(source.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in source)
        {
            result[kvp.Key] = NormalizeValue(kvp.Value);
        }
        return result;
    }

    private static object? NormalizeValue(object? value)
    {
        if (value is not JsonElement element)
        {
            return value;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out var l) => l,
            JsonValueKind.Number when element.TryGetDouble(out var d) => d,
            _ => element.GetRawText()
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_subscriber is not null)
        {
            try
            {
                await _subscriber.UnsubscribeAsync(PubSubChannel, _redisHandler, CommandFlags.FireAndForget).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while unsubscribing from Redis CDC channel '{Channel}'.", PubSubChannelName);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_subscriber is not null)
        {
            try
            {
                _subscriber.Unsubscribe(PubSubChannel, _redisHandler, CommandFlags.FireAndForget);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error while unsubscribing from Redis CDC channel '{Channel}'.", PubSubChannelName);
            }
        }
    }

    public sealed record RedisCdcEnvelope(string InstanceId, CdcEvent Event);
}
