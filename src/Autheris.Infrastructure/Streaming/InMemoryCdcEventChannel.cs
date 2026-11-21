namespace Autheris.Infrastructure.Streaming;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class InMemoryCdcEventChannel : ICdcEventChannel
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Subscriber>> _topicSubscribers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<InMemoryCdcEventChannel> _logger;

    // GQL-8: registering a subscriber and removing an empty topic must not interleave; otherwise a subscriber can be
    // added to a topic dictionary that was just removed and never receives an event. Publishing stays lock-free on the hot path.
    private readonly object _registrationLock = new();

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

    public ValueTask PublishAsync(CdcEvent cdcEvent, CancellationToken ct = default)
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
            if (_topicSubscribers.TryGetValue(topic, out var subscribers))
            {
                foreach (var (subscriberId, sub) in subscribers)
                {
                    if (sub.Channel.Writer.TryWrite(cdcEvent))
                    {
                        totalDispatched++;
                    }
                    else if (Volatile.Read(ref sub.Disposed) == 0 && Interlocked.Exchange(ref sub.Overflow, 1) == 0)
                    {
                        // Slow subscriber buffer overflowed: fail that subscriber explicitly with CdcResyncRequiredException
                        // instead of dropping oldest events silently or stalling the publisher / other subscribers.
                        sub.Channel.Writer.TryComplete();
                        RemoveSubscriber(topic, subscribers, subscriberId);
                    }
                }
            }
        }

        _logger.LogDebug(
            "Published event '{EventId}' to {SubscriberCount} subscribers across topics",
            cdcEvent.EventId, totalDispatched);

        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<CdcEvent> SubscribeAsync(
        string topic,
        CancellationToken ct = default)
    {
        ValidateTopic(topic);
        return new TopicSubscription(this, topic, ct);
    }

    private (Subscriber Subscriber, ConcurrentDictionary<Guid, Subscriber> TopicMap) RegisterSubscriber(string topic)
    {
        var subscriber = new Subscriber(PerSubscriberChannelCapacity);

        lock (_registrationLock)
        {
            if (_topicSubscribers.Count >= MaxTopics && !_topicSubscribers.ContainsKey(topic))
            {
                throw new InvalidOperationException($"CDC event channel limit of {MaxTopics} topics exceeded.");
            }

            var subscribers = _topicSubscribers.GetOrAdd(topic, _ => new ConcurrentDictionary<Guid, Subscriber>());

            if (subscribers.Count >= MaxSubscribersPerTopic)
            {
                throw new InvalidOperationException($"Subscriber limit of {MaxSubscribersPerTopic} per topic '{topic}' exceeded.");
            }

            subscribers.TryAdd(subscriber.Id, subscriber);
            return (subscriber, subscribers);
        }
    }

    private void RemoveSubscriber(string topic, ConcurrentDictionary<Guid, Subscriber> subscribers, Guid subscriberId)
    {
        lock (_registrationLock)
        {
            if (subscribers.TryRemove(subscriberId, out var removed))
            {
                removed.Channel.Writer.TryComplete();
            }

            if (subscribers.IsEmpty)
            {
                // Only remove the dictionary this subscriber used, never a newer one for the same topic.
                _topicSubscribers.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Guid, Subscriber>>(topic, subscribers));
            }
        }
    }

    private sealed class Subscriber(int capacity)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Channel<CdcEvent> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<CdcEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        public int Overflow;
        public int Disposed;
    }

    private sealed class TopicSubscription(InMemoryCdcEventChannel owner, string topic, CancellationToken subscribeCt) : IAsyncEnumerable<CdcEvent>
    {
        public IAsyncEnumerator<CdcEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new TopicEnumerator(owner, topic, subscribeCt, cancellationToken);
    }

    private sealed class TopicEnumerator : IAsyncEnumerator<CdcEvent>
    {
        private readonly InMemoryCdcEventChannel _owner;
        private readonly string _topic;
        private readonly Subscriber _subscriber;
        private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers;
        private readonly CancellationTokenSource? _linkedCts;
        private readonly CancellationToken _ct;
        private readonly HashSet<string> _seenEventIds = new(StringComparer.Ordinal);

        public TopicEnumerator(
            InMemoryCdcEventChannel owner,
            string topic,
            CancellationToken subscribeCt,
            CancellationToken enumeratorCt)
        {
            _owner = owner;
            _topic = topic;
            (_subscriber, _subscribers) = owner.RegisterSubscriber(topic);

            if (subscribeCt.CanBeCanceled && enumeratorCt.CanBeCanceled && subscribeCt != enumeratorCt)
            {
                _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(subscribeCt, enumeratorCt);
                _ct = _linkedCts.Token;
            }
            else
            {
                _ct = enumeratorCt.CanBeCanceled ? enumeratorCt : subscribeCt;
            }
        }

        public CdcEvent Current { get; private set; } = default!;

        public async ValueTask<bool> MoveNextAsync()
        {
            var reader = _subscriber.Channel.Reader;
            while (true)
            {
                while (reader.TryRead(out var cdcEvent))
                {
                    if (_seenEventIds.Add(cdcEvent.EventId))
                    {
                        if (_seenEventIds.Count > 1000)
                        {
                            _seenEventIds.Clear();
                            _seenEventIds.Add(cdcEvent.EventId);
                        }

                        Current = cdcEvent;
                        return true;
                    }
                }

                if (!await reader.WaitToReadAsync(_ct).ConfigureAwait(false))
                {
                    if (Volatile.Read(ref _subscriber.Overflow) != 0)
                    {
                        throw new CdcResyncRequiredException();
                    }

                    return false;
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            Volatile.Write(ref _subscriber.Disposed, 1);
            _owner.RemoveSubscriber(_topic, _subscribers, _subscriber.Id);
            _linkedCts?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
