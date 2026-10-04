namespace Autheris.Infrastructure.Streaming;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class InMemoryCdcEventChannel : ICdcEventChannel
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<CdcEvent>>> _topicSubscribers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<CdcEvent>> _recentHistory =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<InMemoryCdcEventChannel> _logger;

    private const int MaxTopics = 1_000;
    private const int MaxSubscribersPerTopic = 500;
    private const int PerSubscriberChannelCapacity = 1_000;

    public InMemoryCdcEventChannel(ILogger<InMemoryCdcEventChannel> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private static void ValidateTopic(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic) || topic.Length > 128)
        {
            throw new ArgumentException("Invalid CDC topic name.", nameof(topic));
        }
    }

    public async ValueTask PublishAsync(CdcEvent cdcEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cdcEvent);

        var tableTopic = $"cdc_{cdcEvent.Table.ToQualifiedName().ToLowerInvariant()}";
        var simpleTableTopic = $"cdc_{cdcEvent.Table.TableName.ToLowerInvariant()}";
        var globalTopic = "cdc_all";

        var targetTopics = new List<string>(3) { tableTopic };
        if (!string.Equals(tableTopic, simpleTableTopic, StringComparison.OrdinalIgnoreCase))
        {
            targetTopics.Add(simpleTableTopic);
        }
        targetTopics.Add(globalTopic);

        int totalDispatched = 0;
        foreach (var topic in targetTopics)
        {
            var history = _recentHistory.GetOrAdd(topic, _ => new ConcurrentQueue<CdcEvent>());
            history.Enqueue(cdcEvent);
            while (history.Count > 50 && history.TryDequeue(out _)) { }

            if (_topicSubscribers.TryGetValue(topic, out var subscribers))
            {
                foreach (var (_, subChannel) in subscribers)
                {
                    if (!subChannel.Writer.TryWrite(cdcEvent))
                    {
                        await subChannel.Writer.WriteAsync(cdcEvent, ct).ConfigureAwait(false);
                    }
                    totalDispatched++;
                }
            }
        }

        _logger.LogDebug(
            "Published event '{EventId}' to {SubscriberCount} subscribers across topics",
            cdcEvent.EventId, totalDispatched);
    }

    public async IAsyncEnumerable<CdcEvent> SubscribeAsync(
        string topic,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ValidateTopic(topic);

        if (_topicSubscribers.Count >= MaxTopics && !_topicSubscribers.ContainsKey(topic))
        {
            throw new InvalidOperationException($"CDC event channel limit of {MaxTopics} topics exceeded.");
        }

        var subscribers = _topicSubscribers.GetOrAdd(topic, _ => new ConcurrentDictionary<Guid, Channel<CdcEvent>>());

        if (subscribers.Count >= MaxSubscribersPerTopic)
        {
            throw new InvalidOperationException($"Subscriber limit of {MaxSubscribersPerTopic} per topic '{topic}' exceeded.");
        }

        var subscriberId = Guid.NewGuid();
        var channel = Channel.CreateBounded<CdcEvent>(new BoundedChannelOptions(PerSubscriberChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        // Prepopulate with recent history
        if (_recentHistory.TryGetValue(topic, out var pastHistory))
        {
            foreach (var pastEvent in pastHistory)
            {
                channel.Writer.TryWrite(pastEvent);
            }
        }

        subscribers.TryAdd(subscriberId, channel);

        var seenEventIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var reader = channel.Reader;
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var cdcEvent))
                {
                    if (seenEventIds.Add(cdcEvent.EventId))
                    {
                        if (seenEventIds.Count > 1000)
                        {
                            seenEventIds.Clear();
                            seenEventIds.Add(cdcEvent.EventId);
                        }
                        yield return cdcEvent;
                    }
                }
            }
        }
        finally
        {
            if (subscribers.TryRemove(subscriberId, out var removedChannel))
            {
                removedChannel.Writer.TryComplete();
            }

            if (subscribers.IsEmpty)
            {
                _topicSubscribers.TryRemove(topic, out _);
            }
        }
    }
}
