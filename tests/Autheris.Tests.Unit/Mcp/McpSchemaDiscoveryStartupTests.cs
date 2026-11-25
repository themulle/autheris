#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.GraphQL.Directives;
using Autheris.GraphQL.Mcp;
using Autheris.Tests.Common;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using HotChocolate.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #1109: the MCP schema discovery must not block host start on the GraphQL executor build; readiness follows its phase.
/// </summary>
[Trait("Category", "Security")]
public sealed class McpSchemaDiscoveryStartupTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>Signals each timer creation, so the test advances the clock only once the back-off delay is armed.</summary>
    private sealed class SignalingTimeProvider : FakeTimeProvider
    {
        private readonly Channel<TimeSpan> _created = Channel.CreateUnbounded<TimeSpan>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _created.Writer.TryWrite(dueTime);
            return timer;
        }

        public async Task<TimeSpan> NextTimerAsync() => await _created.Reader.ReadAsync().AsTask().WaitAsync(Bound);
    }

    private static IRequestExecutor CreateExecutor()
    {
        var schema = SchemaBuilder.New()
            .AddDirectiveType<McpToolDirectiveType>()
            .AddQueryType(d =>
            {
                d.Name("Query");
                d.Field("lookup_orders")
                    .Type<StringType>()
                    .Resolve(_ => "ok")
                    .Directive("mcpTool", new ArgumentNode("name", "lookup_orders"));
            })
            .Create();

        var executor = Substitute.For<IRequestExecutor>();
        executor.Schema.Returns(schema);
        return executor;
    }

    [Fact]
    public async Task StartAsync_ReturnsBeforeExecutorIsBuilt_PhaseFlipsToReadyAfterBuild()
    {
        var build = new TaskCompletionSource<IRequestExecutor>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = Substitute.For<IRequestExecutorProvider>();
        provider.GetExecutorAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(_ => new ValueTask<IRequestExecutor>(build.Task));
        var registry = new McpToolRegistry();
        var logger = new CapturingLogger<McpSchemaDiscoveryService>();
        using var service = new McpSchemaDiscoveryService(provider, registry, logger);

        // The host start returns at once although the executor build has not finished.
        await service.StartAsync(CancellationToken.None).WaitAsync(Bound);
        service.Phase.ShouldBe(McpSchemaDiscoveryPhase.Pending);
        ((IMcpSchemaDiscoveryState)service).Phase.ShouldBe(McpSchemaDiscoveryPhase.Pending);
        registry.FindTool("lookup_orders").ShouldBeNull();

        build.SetResult(CreateExecutor());
        await service.ExecuteTask!.WaitAsync(Bound);

        service.Phase.ShouldBe(McpSchemaDiscoveryPhase.Ready);
        registry.FindTool("lookup_orders").ShouldNotBeNull();
        logger.Entries.ShouldContain(e => e.EventId.Id == 15131);
        logger.Entries.ShouldContain(e => e.EventId.Id == 15132 && e.Message.Contains("1 dynamic tools", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildFailure_KeepsNotReady_LogsError_RetriesWithBackoff_ThenReady()
    {
        var time = new SignalingTimeProvider();
        var calls = 0;
        var provider = Substitute.For<IRequestExecutorProvider>();
        provider.GetExecutorAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(_ =>
            Interlocked.Increment(ref calls) == 1
                ? ValueTask.FromException<IRequestExecutor>(new InvalidOperationException("schema build failed"))
                : new ValueTask<IRequestExecutor>(CreateExecutor()));
        var registry = new McpToolRegistry();
        var logger = new CapturingLogger<McpSchemaDiscoveryService>();
        using var service = new McpSchemaDiscoveryService(provider, registry, logger, time);

        await service.StartAsync(CancellationToken.None).WaitAsync(Bound);
        (await time.NextTimerAsync()).ShouldBe(McpSchemaDiscoveryService.InitialBackoff);
        service.Phase.ShouldBe(McpSchemaDiscoveryPhase.Failed);

        // The failure never stops the host: the service keeps running and stays not ready until the retry.
        service.ExecuteTask!.IsCompleted.ShouldBeFalse();
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Error && e.EventId.Id == 15133 && e.Exception is InvalidOperationException);
        registry.FindTool("lookup_orders").ShouldBeNull();

        time.Advance(McpSchemaDiscoveryService.InitialBackoff - TimeSpan.FromTicks(1));
        Volatile.Read(ref calls).ShouldBe(1);
        service.Phase.ShouldBe(McpSchemaDiscoveryPhase.Failed);

        time.Advance(TimeSpan.FromTicks(1));
        await service.ExecuteTask.WaitAsync(Bound);

        service.Phase.ShouldBe(McpSchemaDiscoveryPhase.Ready);
        Volatile.Read(ref calls).ShouldBe(2);
        registry.FindTool("lookup_orders").ShouldNotBeNull();
    }

    [Fact]
    public async Task StopAsync_DuringBuild_EndsWithoutReady()
    {
        var provider = Substitute.For<IRequestExecutorProvider>();
        provider.GetExecutorAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ct = ci.ArgAt<CancellationToken>(1);
                var never = new TaskCompletionSource<IRequestExecutor>(TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => never.TrySetCanceled(ct));
                return new ValueTask<IRequestExecutor>(never.Task);
            });
        using var service = new McpSchemaDiscoveryService(provider, new McpToolRegistry(), new CapturingLogger<McpSchemaDiscoveryService>());

        await service.StartAsync(CancellationToken.None).WaitAsync(Bound);
        await service.StopAsync(CancellationToken.None).WaitAsync(Bound);

        service.ExecuteTask!.IsCompleted.ShouldBeTrue();
        service.Phase.ShouldBe(McpSchemaDiscoveryPhase.Pending);
    }
}
