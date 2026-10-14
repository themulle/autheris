using System.Diagnostics.Metrics;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Autheris.Tests.Unit.Messaging;

public sealed class RedisEventBusTests
{
    private sealed record PublishProbeMessage(string Value);
    private sealed record HandlerProbeMessage(string Value);

    private static (RedisEventBus Bus, ISubscriber Subscriber) Create()
    {
        var subscriber = Substitute.For<ISubscriber>();
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetSubscriber(Arg.Any<object?>()).Returns(subscriber);
        return (new RedisEventBus(mux, Options.Create(new GatewayOptions()), NullLogger<RedisEventBus>.Instance), subscriber);
    }

    private static MeterListener Listen(string instrumentName, string messageType, List<long> sink)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == instrumentName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "message_type" && Equals(tag.Value, messageType))
                {
                    lock (sink) { sink.Add(value); }
                }
            }
        });
        listener.Start();
        return listener;
    }

    [Fact]
    public async Task RedisEventBus_PublishFailure_IncrementsPublishFailedCounter_AndRethrows()
    {
        var (bus, subscriber) = Create();
        subscriber.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns<Task<long>>(_ => throw new RedisException("down"));
        var measurements = new List<long>();
        using var listener = Listen("autheris_event_bus_publish_failed_total", nameof(PublishProbeMessage), measurements);

        await Should.ThrowAsync<RedisException>(() => bus.PublishAsync("tenant-secret-channel", new PublishProbeMessage("x")));

        lock (measurements) { measurements.Sum().ShouldBe(1); }
    }

    [Fact]
    public async Task RedisEventBus_PublishFailureMetric_DoesNotCarryTheChannelName()
    {
        var (bus, subscriber) = Create();
        subscriber.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns<Task<long>>(_ => throw new RedisException("down"));
        var tagValues = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "autheris_event_bus_publish_failed_total") { l.EnableMeasurementEvents(instrument); }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags) { lock (tagValues) { tagValues.Add($"{tag.Key}={tag.Value}"); } }
        });
        listener.Start();

        await Should.ThrowAsync<RedisException>(() => bus.PublishAsync("tenant-secret-channel", new PublishProbeMessage("x")));

        lock (tagValues) { tagValues.ShouldNotContain(t => t.Contains("tenant-secret-channel")); }
    }

    [Fact]
    public async Task RedisEventBus_HandlerFailure_IncrementsHandlerFailedCounter()
    {
        var (bus, subscriber) = Create();
        Action<RedisChannel, RedisValue>? onMessage = null;
        subscriber.When(s => s.Subscribe(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>()))
            .Do(ci => onMessage = ci.Arg<Action<RedisChannel, RedisValue>>());
        var measurements = new List<long>();
        using var listener = Listen("autheris_event_bus_handler_failed_total", nameof(HandlerProbeMessage), measurements);
        bus.Subscribe<HandlerProbeMessage>("ch", _ => throw new InvalidOperationException("handler bug"));
        onMessage.ShouldNotBeNull();

        onMessage!(RedisChannel.Literal("Autheris:ch"), "{\"value\":\"x\"}");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (measurements) { if (measurements.Count > 0) { break; } }
            await Task.Delay(20);
        }
        lock (measurements) { measurements.Sum().ShouldBe(1); }
    }
}
