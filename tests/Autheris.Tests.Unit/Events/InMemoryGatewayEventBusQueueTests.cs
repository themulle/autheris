namespace Autheris.Tests.Unit.Events;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events;
using Autheris.Domain.Events;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class InMemoryGatewayEventBusQueueTests
{
    private static InMemoryGatewayEventBus NewBus(int capacity = 1024, TimeSpan? timeout = null) =>
        new(NullLogger<InMemoryGatewayEventBus>.Instance, new EventBusOptions { SubscriberQueueCapacity = capacity }, timeout);

    private static AccessPlanCreatedEvent Ev(string id) =>
        new(id, "plan", "sales.orders", "admin", 1, DateTimeOffset.UtcNow);

    [Fact]
    public async Task PublishAsync_WithHangingHandler_ReturnsImmediately()
    {
        using var bus = NewBus(timeout: TimeSpan.FromSeconds(30));
        using var sub = bus.Subscribe<AccessPlanCreatedEvent>((_, _) => new TaskCompletionSource().Task);

        // The handler never completes and the bus timeout is 30 s: publish only returns promptly if it does not await the handler.
        await bus.PublishAsync(Ev("e1")).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task SlowHandler_DoesNotDelayOtherHandlers()
    {
        using var bus = NewBus(timeout: TimeSpan.FromSeconds(30));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var slow = bus.Subscribe<AccessPlanCreatedEvent>((_, _) => release.Task);
        using var fast = bus.Subscribe<AccessPlanCreatedEvent>((_, _) =>
        {
            fastDone.TrySetResult();
            return Task.CompletedTask;
        });

        await bus.PublishAsync(Ev("e1"));

        await fastDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
    }

    [Fact]
    public async Task CancelledPublisherToken_StillDeliversEvent()
    {
        using var bus = NewBus();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = bus.Subscribe<AccessPlanCreatedEvent>((ev, _) =>
        {
            received.TrySetResult(ev.EventId);
            return Task.CompletedTask;
        });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await bus.PublishAsync(Ev("e-cancel"), cts.Token);

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("e-cancel");
    }

    [Fact]
    public async Task FullQueue_DropsOldest_AndKeepsNewest()
    {
        using var bus = NewBus(capacity: 2, timeout: TimeSpan.FromSeconds(30));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>();
        using var sub = bus.Subscribe<AccessPlanCreatedEvent>(async (ev, _) =>
        {
            lock (seen)
            {
                seen.Add(ev.EventId);
            }

            started.TrySetResult();
            await gate.Task;
        });

        await bus.PublishAsync(Ev("e1"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); // e1 is in-flight; queue is empty
        for (var i = 2; i <= 5; i++)
        {
            await bus.PublishAsync(Ev($"e{i}"));
        }

        gate.SetResult();
        await bus.DrainAsync();

        lock (seen)
        {
            seen.ShouldBe(["e1", "e4", "e5"]);
        }
    }

    [Fact]
    public async Task Subscribe_Unsubscribe_UpdatesCachedHandlerArray()
    {
        using var bus = NewBus();
        var count = 0;
        await bus.PublishAsync(Ev("warm-cache"));

        var token = bus.Subscribe<AccessPlanCreatedEvent>((_, _) =>
        {
            Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });
        await bus.PublishAsync(Ev("e1"));
        await bus.DrainAsync();
        count.ShouldBe(1);

        token.Dispose();
        await bus.PublishAsync(Ev("e2"));
        await bus.DrainAsync();
        count.ShouldBe(1);
    }

    [Fact]
    public async Task Inline_Mode_AwaitsHandlers()
    {
        using var bus = new InMemoryGatewayEventBus(
            NullLogger<InMemoryGatewayEventBus>.Instance,
            new EventBusOptions { DeliveryMode = EventBusDeliveryMode.Inline });
        var ran = false;
        using var sub = bus.Subscribe<AccessPlanCreatedEvent>((_, _) =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        await bus.PublishAsync(Ev("e1"));

        ran.ShouldBeTrue();
    }

    [Fact]
    public async Task HandlerTimeout_DoesNotBlockSubsequentEventsOfSameSubscriber()
    {
        using var bus = NewBus(timeout: TimeSpan.FromMilliseconds(100));
        var first = true;
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = bus.Subscribe<AccessPlanCreatedEvent>((ev, _) =>
        {
            if (first)
            {
                first = false;
                return new TaskCompletionSource().Task;
            }

            second.TrySetResult();
            return Task.CompletedTask;
        });

        await bus.PublishAsync(Ev("e1"));
        await bus.PublishAsync(Ev("e2"));

        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
