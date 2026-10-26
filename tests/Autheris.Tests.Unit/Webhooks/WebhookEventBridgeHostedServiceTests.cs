namespace Autheris.Tests.Unit.Webhooks;

using Autheris.Tests.Common;
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

        // The dispatcher blocks until released: publish only returns if it does not wait for delivery.
        await bus.PublishAsync(NewEvent("ev-slow")).WaitAsync(WaitHelper.HangGuard);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await bridge.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Publish_DispatcherFailure_IsContainedAndLoggedWithoutMessage()
    {
        var bus = new InMemoryGatewayEventBus(NullLogger<InMemoryGatewayEventBus>.Instance);
        var dispatcher = Substitute.For<IWebhookDispatcherService>();
        dispatcher.DispatchEventAsync(Arg.Any<IGatewayEvent>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("Password=hunter2"));
        var logger = new WebhookCapturingLogger<WebhookEventBridgeHostedService>();
        var bridge = new WebhookEventBridgeHostedService(bus, dispatcher, logger);
        await bridge.StartAsync(CancellationToken.None);

        await bus.PublishAsync(NewEvent("ev-fail"));
        await bus.DrainAsync();

        logger.All.ShouldContain("ev-fail");
        logger.All.ShouldNotContain("hunter2");
        await bridge.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Publish_EnqueuesEachEventThroughTheDispatcher()
    {
        var bus = new InMemoryGatewayEventBus(NullLogger<InMemoryGatewayEventBus>.Instance);
        var dispatcher = Substitute.For<IWebhookDispatcherService>();
        var bridge = new WebhookEventBridgeHostedService(bus, dispatcher, NullLogger<WebhookEventBridgeHostedService>.Instance);
        await bridge.StartAsync(CancellationToken.None);

        await bus.PublishAsync(NewEvent("ev-1"));
        await bus.PublishAsync(NewEvent("ev-2"));
        await bus.DrainAsync();

        await dispatcher.Received(2).DispatchEventAsync(Arg.Any<IGatewayEvent>(), Arg.Any<CancellationToken>());
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

        // The hanging subscriber never completes: publish only returns if the bus enforces its 200 ms subscriber timeout.
        await bus.PublishAsync(NewEvent("ev-hang")).WaitAsync(WaitHelper.HangGuard);

        await bus.DrainAsync();
        healthyRan.ShouldBeTrue();
    }
}
