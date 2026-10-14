namespace Autheris.Tests.Unit.Plugins;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events;
using Autheris.Application.Plugins;
using Autheris.Domain.Events;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    private sealed class StackCapturingListener : IPluginEventListener<AccessPlanCreatedEvent>
    {
        public StackTrace? Stack { get; private set; }

        public Task HandleEventAsync(AccessPlanCreatedEvent @event, CancellationToken ct = default)
        {
            Stack = new StackTrace();
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingListener : IPluginEventListener<AccessPlanCreatedEvent>
    {
        public Task HandleEventAsync(AccessPlanCreatedEvent @event, CancellationToken ct = default) =>
            throw new InvalidOperationException("listener boom");
    }

    private sealed class TwoEventListener : IPluginEventListener<AccessPlanCreatedEvent>, IPluginEventListener<AccessPlanAppliedEvent>
    {
        public List<string> Received { get; } = [];

        public Task HandleEventAsync(AccessPlanCreatedEvent @event, CancellationToken ct = default)
        {
            Received.Add("created:" + @event.EventId);
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(AccessPlanAppliedEvent @event, CancellationToken ct = default)
        {
            Received.Add("applied:" + @event.EventId);
            return Task.CompletedTask;
        }
    }

    private sealed class ExceptionCapturingLogger : ILogger<PluginManager>
    {
        public List<Exception> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception != null)
            {
                Exceptions.Add(exception);
            }
        }
    }

    private static (PluginManager Manager, InMemoryGatewayEventBus Bus) CreateManager(ILogger<PluginManager>? logger = null)
    {
        var services = new ServiceCollection();
        var bus = new InMemoryGatewayEventBus(NullLogger<InMemoryGatewayEventBus>.Instance);
        services.AddSingleton<IGatewayEventBus>(bus);
        var manager = new PluginManager(logger ?? NullLogger<PluginManager>.Instance, services.BuildServiceProvider(), Options.Create(new GatewayOptions()));
        return (manager, bus);
    }

    private static AccessPlanCreatedEvent Created(string id) =>
        new(EventId: id, PlanId: "p", DatasetId: "d", RequesterSid: "s", GrantCount: 1, Timestamp: DateTimeOffset.UtcNow);

    [Fact]
    public async Task PluginListener_IsInvokedWithoutReflectionPerEvent()
    {
        var (manager, bus) = CreateManager();
        var listener = new StackCapturingListener();
        manager.RegisterListener(listener);

        await bus.PublishAsync(Created("ev-1"));
        await bus.DrainAsync();

        listener.Stack.ShouldNotBeNull();
        var reflectionFrames = listener.Stack!.GetFrames()
            .Select(f => f.GetMethod()?.DeclaringType?.Namespace)
            .Where(ns => ns != null && ns.StartsWith("System.Reflection", StringComparison.Ordinal))
            .ToList();
        reflectionFrames.ShouldBeEmpty("a listener must be called through a compiled delegate, not MethodInfo.Invoke");
    }

    [Fact]
    public async Task PluginListener_ThrowingHandler_LogsOriginalException_NotTargetInvocationException()
    {
        var logger = new ExceptionCapturingLogger();
        var (manager, bus) = CreateManager(logger);
        manager.RegisterListener(new ThrowingListener());

        await bus.PublishAsync(Created("ev-2"));
        await bus.DrainAsync();

        var logged = logger.Exceptions.ShouldHaveSingleItem();
        logged.ShouldBeOfType<InvalidOperationException>();
        logged.Message.ShouldBe("listener boom");
    }

    [Fact]
    public async Task PluginListener_ImplementingTwoEventInterfaces_ReceivesBoth()
    {
        var (manager, bus) = CreateManager();
        var listener = new TwoEventListener();
        manager.RegisterListener(listener);

        await bus.PublishAsync(Created("ev-3"));
        await bus.PublishAsync(new AccessPlanAppliedEvent(EventId: "ev-4", PlanId: "p", DatasetId: "d", ApproverSid: "s", AppliedGrantCount: 1, Timestamp: DateTimeOffset.UtcNow));
        await bus.DrainAsync();

        listener.Received.ShouldBe(["created:ev-3", "applied:ev-4"], ignoreOrder: true);
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
