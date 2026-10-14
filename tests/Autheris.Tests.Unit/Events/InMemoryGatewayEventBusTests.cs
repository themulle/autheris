namespace Autheris.Tests.Unit.Events;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events;
using Autheris.Domain.Events;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class InMemoryGatewayEventBusTests
{
    private readonly InMemoryGatewayEventBus _bus;

    public InMemoryGatewayEventBusTests()
    {
        _bus = new InMemoryGatewayEventBus(NullLogger<InMemoryGatewayEventBus>.Instance);
    }

    [Fact]
    public async Task PublishAsync_WithTypedSubscriber_DeliversEvent()
    {
        // Arrange
        AccessPlanCreatedEvent? received = null;
        using var sub = _bus.Subscribe<AccessPlanCreatedEvent>((ev, ct) =>
        {
            received = ev;
            return Task.CompletedTask;
        });

        var @event = new AccessPlanCreatedEvent(
            EventId: "ev-1",
            PlanId: "plan-1",
            DatasetId: "sales.orders",
            RequesterSid: "admin-1",
            GrantCount: 3,
            Timestamp: DateTimeOffset.UtcNow);

        // Act
        await _bus.PublishAsync(@event);

        // Assert
        received.ShouldNotBeNull();
        received!.EventId.ShouldBe("ev-1");
        received.PlanId.ShouldBe("plan-1");
        received.DatasetId.ShouldBe("sales.orders");
    }

    [Fact]
    public async Task PublishAsync_WithUniversalSubscriber_DeliversAnyGatewayEvent()
    {
        // Arrange
        var receivedEvents = new List<IGatewayEvent>();
        using var sub = _bus.Subscribe<IGatewayEvent>((ev, ct) =>
        {
            lock (receivedEvents)
            {
                receivedEvents.Add(ev);
            }
            return Task.CompletedTask;
        });

        var ev1 = new AccessPlanCreatedEvent("ev-1", "plan-1", "sales.orders", "admin", 1, DateTimeOffset.UtcNow);
        var ev2 = new SecurityAlertEvent("ev-2", "PROMPT_INJECTION", "HIGH", "Injection test", "127.0.0.1", "user-1", DateTimeOffset.UtcNow);

        // Act
        await _bus.PublishAsync(ev1);
        await _bus.PublishAsync(ev2);

        // Assert
        receivedEvents.Count.ShouldBe(2);
        receivedEvents[0].EventId.ShouldBe("ev-1");
        receivedEvents[1].EventId.ShouldBe("ev-2");
    }

    [Fact]
    public async Task PublishAsync_WhenOneSubscriberThrows_OtherSubscribersStillExecuted()
    {
        // Arrange
        bool healthySubscriberExecuted = false;

        using var faultySub = _bus.Subscribe<AccessPlanCreatedEvent>((ev, ct) =>
            throw new InvalidOperationException("Subscriber failure simulation"));

        using var healthySub = _bus.Subscribe<AccessPlanCreatedEvent>((ev, ct) =>
        {
            healthySubscriberExecuted = true;
            return Task.CompletedTask;
        });

        var @event = new AccessPlanCreatedEvent("ev-err", "plan-err", "sales.orders", "admin", 1, DateTimeOffset.UtcNow);

        // Act & Assert
        await Should.NotThrowAsync(async () => await _bus.PublishAsync(@event));
        healthySubscriberExecuted.ShouldBeTrue();
    }

    [Fact]
    public async Task Subscribe_DisposingToken_UnsubscribesHandler()
    {
        // Arrange
        int invocationCount = 0;
        var token = _bus.Subscribe<AccessPlanCreatedEvent>((ev, ct) =>
        {
            invocationCount++;
            return Task.CompletedTask;
        });

        var @event = new AccessPlanCreatedEvent("ev-unsub", "plan-1", "sales.orders", "admin", 1, DateTimeOffset.UtcNow);

        // Act 1
        await _bus.PublishAsync(@event);
        invocationCount.ShouldBe(1);

        // Dispose subscription
        token.Dispose();

        // Act 2
        await _bus.PublishAsync(@event);

        // Assert
        invocationCount.ShouldBe(1);
    }
}
