namespace Autheris.Tests.Unit.Webhooks;

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events;
using Autheris.Application.Webhooks;
using Autheris.Domain.Events;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class WebhookEventBridgeHostedServiceTests
{
    private static AccessPlanCreatedEvent NewEvent(string id) =>
        new(id, "plan", "sales.orders", "admin", 1, DateTimeOffset.UtcNow, "tenant-a");

    [Fact]
    public async Task SR_M_13_Publish_DoesNotWaitForSlowWebhookDelivery()
    {
        var bus = new InMemoryGatewayEventBus(NullLogger<InMemoryGatewayEventBus>.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IWebhookDispatcherService>();
        dispatcher.DispatchEventAsync(Arg.Any<IGatewayEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                started.TrySetResult();
                return release.Task;
            });

        var bridge = new WebhookEventBridgeHostedService(bus, dispatcher, NullLogger<WebhookEventBridgeHostedService>.Instance);
        await bridge.StartAsync(CancellationToken.None);

        var sw = Stopwatch.StartNew();
        await bus.PublishAsync(NewEvent("ev-slow"));
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await bridge.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SR_M_13_Publish_WhenQueueFull_DropsInsteadOfBlocking()
    {
        var bus = new InMemoryGatewayEventBus(NullLogger<InMemoryGatewayEventBus>.Instance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = Substitute.For<IWebhookDispatcherService>();
        dispatcher.DispatchEventAsync(Arg.Any<IGatewayEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => release.Task);

        var bridge = new WebhookEventBridgeHostedService(bus, dispatcher, NullLogger<WebhookEventBridgeHostedService>.Instance, queueCapacity: 1);
        await bridge.StartAsync(CancellationToken.None);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 50; i++)
        {
            await bus.PublishAsync(NewEvent($"ev-{i}"));
        }

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));

        release.SetResult();
        await bridge.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SR_M_13_Bus_HangingSubscriber_IsTimedOut()
    {
        var bus = new InMemoryGatewayEventBus(NullLogger<InMemoryGatewayEventBus>.Instance, TimeSpan.FromMilliseconds(200));
        using var hang = bus.Subscribe<AccessPlanCreatedEvent>((_, _) => new TaskCompletionSource().Task);
        var healthyRan = false;
        using var healthy = bus.Subscribe<AccessPlanCreatedEvent>((_, _) =>
        {
            healthyRan = true;
            return Task.CompletedTask;
        });

        var sw = Stopwatch.StartNew();
        await bus.PublishAsync(NewEvent("ev-hang"));

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        await bus.DrainAsync();
        healthyRan.ShouldBeTrue();
    }
}
