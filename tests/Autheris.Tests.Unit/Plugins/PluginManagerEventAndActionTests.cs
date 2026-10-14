namespace Autheris.Tests.Unit.Plugins;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events;
using Autheris.Application.Plugins;
using Autheris.Domain.Events;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class PluginManagerEventAndActionTests
{
    private sealed class TestEventListener : IPluginEventListener<AccessPlanCreatedEvent>
    {
        public AccessPlanCreatedEvent? LastReceivedEvent { get; private set; }

        public Task HandleEventAsync(AccessPlanCreatedEvent @event, CancellationToken ct = default)
        {
            LastReceivedEvent = @event;
            return Task.CompletedTask;
        }
    }

    private sealed class TestActionHandler : IPluginActionHandler
    {
        public string ActionName => "test.echo";

        public Task<PluginActionResult> ExecuteActionAsync(PluginActionContext context, CancellationToken ct = default)
        {
            if (context.Parameters.TryGetValue("message", out var msg))
            {
                return Task.FromResult(PluginActionResult.Succeeded(new { echo = msg }));
            }

            return Task.FromResult(PluginActionResult.Failed("Parameter 'message' is missing."));
        }
    }

    [Fact]
    public async Task RegisterListener_BindsDirectlyToEventBus()
    {
        // Arrange
        var services = new ServiceCollection();
        var bus = new InMemoryGatewayEventBus(NullLogger<InMemoryGatewayEventBus>.Instance);
        services.AddSingleton<IGatewayEventBus>(bus);
        var sp = services.BuildServiceProvider();

        var pluginManager = new PluginManager(
            NullLogger<PluginManager>.Instance,
            sp,
            Options.Create(new GatewayOptions()));

        var listener = new TestEventListener();

        // Act
        pluginManager.RegisterListener(listener);

        var @event = new AccessPlanCreatedEvent(
            EventId: "ev-999",
            PlanId: "plan-999",
            DatasetId: "crm.customers",
            RequesterSid: "sec-admin",
            GrantCount: 5,
            Timestamp: DateTimeOffset.UtcNow);

        await bus.PublishAsync(@event);
        await bus.DrainAsync();

        // Assert
        listener.LastReceivedEvent.ShouldNotBeNull();
        listener.LastReceivedEvent!.EventId.ShouldBe("ev-999");
        listener.LastReceivedEvent.DatasetId.ShouldBe("crm.customers");
    }

    [Fact]
    public async Task ExecuteActionAsync_WithRegisteredHandler_ExecutesSuccessfully()
    {
        // Arrange
        var pluginManager = new PluginManager(
            NullLogger<PluginManager>.Instance,
            null,
            Options.Create(new GatewayOptions()));

        var handler = new TestActionHandler();
        pluginManager.RegisterActionHandler(handler);

        var context = new PluginActionContext(
            ActionName: "test.echo",
            Parameters: new Dictionary<string, object?> { ["message"] = "hello plugin world" },
            Principal: "user-123");

        // Act
        var result = await pluginManager.ExecuteActionAsync("test.echo", context);

        // Assert
        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
        result.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteActionAsync_WhenHandlerMissing_ReturnsFailure()
    {
        // Arrange
        var pluginManager = new PluginManager(
            NullLogger<PluginManager>.Instance,
            null,
            Options.Create(new GatewayOptions()));

        var context = new PluginActionContext("unknown.action", new Dictionary<string, object?>());

        // Act
        var result = await pluginManager.ExecuteActionAsync("unknown.action", context);

        // Assert
        result.ShouldNotBeNull();
        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("not found");
    }
}
