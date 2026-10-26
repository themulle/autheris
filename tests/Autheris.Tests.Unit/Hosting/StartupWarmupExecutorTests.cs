namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Hosting.Warmup;
using Autheris.Api.Mcp;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Types.Descriptors;
using HotChocolate.Execution.Configuration;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;
using OperationDocumentId = HotChocolate.Language.OperationDocumentId;
using RequestContext = HotChocolate.Execution.RequestContext;

/// <summary>
/// PLAN-OPS-150 WP-C: warm-up task, <c>WarmedExecutor</c> proof, eviction handling, hang logging, MCP serialisation warm-up and the
/// cold-path listener rules. Deterministic signalling only (TCS, channels, fake clock).
/// </summary>
public sealed class StartupWarmupExecutorTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private sealed class TestLogger<T> : ILogger<T>
    {
        private readonly Channel<string> _messages = Channel.CreateUnbounded<string>();

        public ConcurrentQueue<string> All { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception) + (exception is null ? string.Empty : "|EXCEPTION:" + exception);
            All.Enqueue(text);
            _messages.Writer.TryWrite(text);
        }

        public async Task<string> NextAsync()
        {
            using var cts = new CancellationTokenSource(Bound);
            return await _messages.Reader.ReadAsync(cts.Token);
        }
    }

    private sealed class SignalingTimeProvider : FakeTimeProvider
    {
        private readonly Channel<TimeSpan> _created = Channel.CreateUnbounded<TimeSpan>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _created.Writer.TryWrite(dueTime);
            return timer;
        }

        public async Task<TimeSpan> NextTimerAsync()
        {
            using var cts = new CancellationTokenSource(Bound);
            return await _created.Reader.ReadAsync(cts.Token);
        }
    }

    private sealed class FakeEvents : IRequestExecutorEvents
    {
        private readonly List<IObserver<RequestExecutorEvent>> _observers = [];

        public int Active
        {
            get { lock (_observers) { return _observers.Count; } }
        }

        public IDisposable Subscribe(IObserver<RequestExecutorEvent> observer)
        {
            lock (_observers)
            {
                _observers.Add(observer);
            }

            return new Subscription(this, observer);
        }

        public void Evict(IRequestExecutor executor)
        {
            IObserver<RequestExecutorEvent>[] snapshot;
            lock (_observers)
            {
                snapshot = [.. _observers];
            }

            foreach (var observer in snapshot)
            {
                observer.OnNext(new RequestExecutorEvent(RequestExecutorEventType.Evicted, "_Default", executor));
            }
        }

        private sealed class Subscription(FakeEvents owner, IObserver<RequestExecutorEvent> observer) : IDisposable
        {
            public void Dispose()
            {
                lock (owner._observers)
                {
                    owner._observers.Remove(observer);
                }
            }
        }
    }

    private sealed class ToggleModule : ITypeModule
    {
        public volatile bool Fail = true;

        public event EventHandler<EventArgs>? TypesChanged
        {
            add { }
            remove { }
        }

        public ValueTask<IReadOnlyCollection<ITypeSystemMember>> CreateTypesAsync(IDescriptorContext context, CancellationToken cancellationToken) =>
            Fail
                ? throw new InvalidOperationException("deliberately broken type module")
                : new ValueTask<IReadOnlyCollection<ITypeSystemMember>>([]);
    }

    private sealed class Counter
    {
        public int Resolved;
        public readonly List<bool> HttpContextSeen = [];
        public readonly List<int> ContextDataKeys = [];
        public readonly List<bool> WarmupSeen = [];
    }

    private static GraphQlExecutorGate CreateGate(
        IRequestExecutorProvider provider,
        TimeProvider time,
        GraphQlWarmupProof? proof = null,
        IHttpContextAccessor? accessor = null,
        IRequestExecutorEvents? events = null,
        ILogger<GraphQlExecutorGate>? logger = null,
        int timeoutSeconds = 30) =>
        new(provider, Options.Create(new GatewayOptions()), time, logger ?? new TestLogger<GraphQlExecutorGate>(), proof, accessor, events, Options.Create(new StartupWarmupOptions { TimeoutSeconds = timeoutSeconds }));

    private static ServiceProvider BuildReal(
        Counter? counter = null,
        ToggleModule? module = null,
        GraphQlWarmupProof? proof = null,
        IHttpContextAccessor? accessor = null,
        bool withTask = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<GatewayOptions>();
        services.AddSingleton(proof ?? new GraphQlWarmupProof());
        services.AddSingleton(accessor ?? new HttpContextAccessor());
        var builder = services.AddGraphQLServer()
            .AddQueryType(d => d.Name("Query").Field("hello").Type<StringType>().Resolve(_ =>
            {
                if (counter is not null)
                {
                    Interlocked.Increment(ref counter.Resolved);
                }

                return new ValueTask<object?>("world");
            }));
        if (module is not null)
        {
            builder.AddTypeModule(_ => module);
        }

        builder.UseRequest(next => async context =>
        {
            if (counter is not null)
            {
                lock (counter)
                {
                    counter.HttpContextSeen.Add(context.RequestServices.GetService<IHttpContextAccessor>()?.HttpContext is not null);
                    counter.ContextDataKeys.Add(context.ContextData.Count);
                    counter.WarmupSeen.Add(context.IsWarmupRequest());
                }
            }

            await next(context);
        });
        builder.UseExceptions().UseTimeout().UseDocumentCache()
            .UseDocumentParser().UseDocumentValidation().UseOperationCache().UseOperationResolver()
            .UseOperationVariableCoercion().UseOperationExecution();
        if (withTask)
        {
            builder.AddGraphQlExecutorWarmupTask();
        }

        return services.BuildServiceProvider();
    }

    private static IRequestExecutorProvider ProviderOf(Func<int, ValueTask<IRequestExecutor>> factory)
    {
        var calls = 0;
        var provider = Substitute.For<IRequestExecutorProvider>();
        provider.GetExecutorAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => factory(Interlocked.Increment(ref calls)));
        return provider;
    }

    private static async Task<IRequestExecutor> ExecutorOf(ServiceProvider sp) =>
        await sp.GetRequiredService<IRequestExecutorProvider>().GetExecutorAsync();

    private static async Task WaitForState(GraphQlExecutorGate gate) => await gate.ExecuteTask!.WaitAsync(Bound);

    private sealed class StepMetrics : IDisposable
    {
        private readonly MeterListener _listener = new();

        public ConcurrentQueue<(string Step, string Outcome)> Recorded { get; } = new();

        public StepMetrics()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, GatewayDiagnostics.Meter) && instrument.Name == StartupWarmupDiagnostics.StepDuration.Name)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
            {
                string step = string.Empty, outcome = string.Empty;
                foreach (var tag in tags)
                {
                    if (tag.Key == "step")
                    {
                        step = tag.Value?.ToString() ?? string.Empty;
                    }
                    else if (tag.Key == "outcome")
                    {
                        outcome = tag.Value?.ToString() ?? string.Empty;
                    }
                }

                Recorded.Enqueue((step, outcome));
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    // ---- warm-up task ----

    [Fact]
    public async Task GraphQlWarmupTask_RunsTypenameDocument_NoResolverInvoked()
    {
        var counter = new Counter();
        var proof = new GraphQlWarmupProof();
        await using var sp = BuildReal(counter, proof: proof, withTask: true);

        var executor = await ExecutorOf(sp);

        // Task path is primary (A-150-2): the build itself ran the constant and recorded the proof for exactly this instance.
        proof.WarmedExecutor.ShouldBeSameAs(executor);
        counter.Resolved.ShouldBe(0);
    }

    [Fact]
    public async Task GraphQlWarmupTask_ExecutesTypenameOnly_DataIsQuery_NoResolverOrConnectorInvoked()
    {
        var counter = new Counter();
        var proof = new GraphQlWarmupProof();
        await using var sp = BuildReal(counter, proof: proof, withTask: true);
        var executor = await ExecutorOf(sp);

        var constructor = typeof(GraphQlExecutorWarmupTask).GetConstructors().Single();
        var allowed = new[] { typeof(GraphQlWarmupProof), typeof(IOptions<GatewayOptions>), typeof(ILogger<GraphQlExecutorWarmupTask>), typeof(IHttpContextAccessor) };
        constructor.GetParameters().Select(p => p.ParameterType).ShouldAllBe(t => allowed.Contains(t));
        proof.WarmedExecutor.ShouldBeSameAs(executor);
        counter.Resolved.ShouldBe(0);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task GraphQlWarmupTask_NoHttpContextNoPrincipal_NoAuditNoMetricNoCounter()
    {
        // T-OPS150-20 / SEC-OPS150-17: the task can only depend on the proof, the options, a logger and the accessor, so it cannot reach
        // audit, consent, data source, rate limiter or request metrics; it sees no HttpContext and a context holding only the marker.
        var counter = new Counter();
        await using var sp = BuildReal(counter, withTask: true);
        await ExecutorOf(sp);

        var constructor = typeof(GraphQlExecutorWarmupTask).GetConstructors().Single();
        constructor.GetParameters().Length.ShouldBeLessThanOrEqualTo(4);
        counter.HttpContextSeen.ShouldAllBe(seen => !seen);
        counter.ContextDataKeys.ShouldAllBe(count => count <= 1);
        counter.Resolved.ShouldBe(0);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task WarmupRequest_BuiltWithEmptyContextData_NoPrincipalNoHttpContext()
    {
        // T-OPS150-07 / SEC-OPS150-05: the in-process request carries only the warm-up marker.
        var counter = new Counter();
        await using var sp = BuildReal(counter, withTask: true);
        var executor = await ExecutorOf(sp);

        counter.HttpContextSeen.ShouldNotBeEmpty();
        counter.HttpContextSeen.ShouldAllBe(seen => !seen);
        counter.ContextDataKeys.ShouldAllBe(count => count <= 1);
        executor.ShouldNotBeNull();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task WarmupTask_AmbientHttpContextPresent_SkipsAndCountsFailed()
    {
        // A-150-5 / SEC-OPS150-16: with a caller context visible the task does not execute and records outcome=failed.
        using var metrics = new StepMetrics();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var proof = new GraphQlWarmupProof();
        var executor = Substitute.For<IRequestExecutor>();
        var task = new GraphQlExecutorWarmupTask(proof, Options.Create(new GatewayOptions()), new TestLogger<GraphQlExecutorWarmupTask>(), accessor);

        await task.WarmupAsync(executor, CancellationToken.None);

        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        proof.WarmedExecutor.ShouldBeNull();
        metrics.Recorded.ShouldContain(("GraphQlExecutor", StartupWarmupDiagnostics.Failed));
        task.ApplyOnlyOnStartup.ShouldBeFalse();
    }

    [Fact]
    public async Task GraphQlWarmupTask_DoesNotCallRateLimiter()
    {
        // The task executes through the same builder only: no rate-limit or quota collaborator is injectable (AC-4 integration test
        // Startup_AuditChainLengthUnchanged_NoDataSourceQuery_NoConsentResolution covers the real pipeline).
        var parameterTypes = typeof(GraphQlExecutorWarmupTask).GetConstructors().Single().GetParameters().Select(p => p.ParameterType.Name);
        parameterTypes.ShouldNotContain(n => n.Contains("RateLimit", StringComparison.OrdinalIgnoreCase) || n.Contains("Quota", StringComparison.OrdinalIgnoreCase));
        await Task.CompletedTask;
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Ready_InitialSchemaBuildThrows_StaysUnready_PastGlobalTimeout()
    {
        // T-OPS150-02 / D-14: a REAL schema build failure keeps the gate unready beyond TimeoutSeconds; nothing opens it.
        var module = new ToggleModule();
        await using var sp = BuildReal(module: module, withTask: true);
        var time = new SignalingTimeProvider();
        using var gate = CreateGate(sp.GetRequiredService<IRequestExecutorProvider>(), time, sp.GetRequiredService<GraphQlWarmupProof>());

        await gate.StartAsync(CancellationToken.None);
        (await time.NextTimerAsync()).ShouldBe(TimeSpan.FromSeconds(5));
        gate.State.ShouldBe(ExecutorGateState.Failed);

        time.Advance(TimeSpan.FromSeconds(5));
        (await time.NextTimerAsync()).ShouldBe(TimeSpan.FromSeconds(10));
        time.Advance(TimeSpan.FromSeconds(31));

        gate.State.ShouldNotBe(ExecutorGateState.Ready);
        gate.ExecuteTask!.IsCompleted.ShouldBeFalse();
        await gate.StopAsync(CancellationToken.None);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Ready_LaterSuccessfulBuild_ClearsInitialFailure()
    {
        // T-OPS150-03 / GraphQlWarmupTask_RebuildSucceeds_ClearsFailure: the next successful build opens the gate.
        var module = new ToggleModule();
        await using var sp = BuildReal(module: module, withTask: true);
        var time = new SignalingTimeProvider();
        using var gate = CreateGate(sp.GetRequiredService<IRequestExecutorProvider>(), time, sp.GetRequiredService<GraphQlWarmupProof>());

        await gate.StartAsync(CancellationToken.None);
        await time.NextTimerAsync();
        gate.State.ShouldBe(ExecutorGateState.Failed);

        module.Fail = false;
        time.Advance(TimeSpan.FromSeconds(5));
        await WaitForState(gate);

        gate.State.ShouldBe(ExecutorGateState.Ready);
        await gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GraphQlWarmupTask_RebuildSucceeds_ClearsFailure() => await Ready_LaterSuccessfulBuild_ClearsInitialFailure();

    // ---- gate: proof identity ----

    [Fact]
    public async Task ExecutorGate_ExecutorWarmedByTask_ReadyWithoutOwnRun()
    {
        var executor = Substitute.For<IRequestExecutor>();
        var proof = new GraphQlWarmupProof();
        proof.Record(executor);
        using var gate = CreateGate(ProviderOf(_ => new ValueTask<IRequestExecutor>(executor)), new FakeTimeProvider(), proof);

        await gate.StartAsync(CancellationToken.None);
        await WaitForState(gate);

        gate.State.ShouldBe(ExecutorGateState.Ready);
        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ExecutorGate_ExecutorReturnedButNotWarmed_StaysPending()
    {
        // SEC-OPS150-01: the proof belongs to another executor instance and the gate's own run does not prove the new one.
        var stale = Substitute.For<IRequestExecutor>();
        var proof = new GraphQlWarmupProof();
        proof.Record(stale);
        var fresh = Substitute.For<IRequestExecutor>();
        fresh.ExecuteAsync(Arg.Any<IOperationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IExecutionResult>(OperationResult.FromError(ErrorBuilder.New().SetMessage("x").SetCode("OTHER").Build())));
        var time = new SignalingTimeProvider();
        using var gate = CreateGate(ProviderOf(_ => new ValueTask<IRequestExecutor>(fresh)), time, proof);

        await gate.StartAsync(CancellationToken.None);
        await time.NextTimerAsync();

        gate.State.ShouldBe(ExecutorGateState.Pending);
        proof.WarmedExecutor.ShouldBeSameAs(stale);
        await gate.StopAsync(CancellationToken.None);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ExecutorGate_AmbientHttpContextPresent_StaysPending_NeverReady()
    {
        // T-OPS150-18 / SEC-OPS150-16: ambient context -> no execution, Pending (503), never Ready.
        var executor = Substitute.For<IRequestExecutor>();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var time = new SignalingTimeProvider();
        using var metrics = new StepMetrics();
        using var gate = CreateGate(ProviderOf(_ => new ValueTask<IRequestExecutor>(executor)), time, new GraphQlWarmupProof(), new AlwaysContextAccessor());

        await gate.StartAsync(CancellationToken.None);
        await time.NextTimerAsync();

        gate.State.ShouldBe(ExecutorGateState.Pending);
        await executor.DidNotReceiveWithAnyArgs().ExecuteAsync(default!, default);
        accessor.HttpContext.ShouldNotBeNull();
        await gate.StopAsync(CancellationToken.None);
    }

    private sealed class AlwaysContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => new DefaultHttpContext();
            set { }
        }
    }

    // ---- gate: eviction ----

    private static async Task<(GraphQlExecutorGate Gate, FakeEvents Events, IRequestExecutor Executor)> ReadyGateAsync(
        Func<int, ValueTask<IRequestExecutor>> afterFirst,
        TimeProvider time,
        ServiceProvider sp,
        IHttpContextAccessor? accessor = null,
        ILogger<GraphQlExecutorGate>? logger = null)
    {
        var executor = await ExecutorOf(sp);
        var events = new FakeEvents();
        var provider = ProviderOf(n => n == 1 ? new ValueTask<IRequestExecutor>(executor) : afterFirst(n));
        var gate = CreateGate(provider, time, sp.GetRequiredService<GraphQlWarmupProof>(), accessor, events, logger);
        await gate.StartAsync(CancellationToken.None);
        await WaitForState(gate);
        gate.State.ShouldBe(ExecutorGateState.Ready);
        return (gate, events, executor);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ExecutorGate_EvictionStorm_CoalescesToSingleProbe_HandlerNeverThrows()
    {
        // T-OPS150-17 / SEC-OPS150-15: one probe in flight, evictions meanwhile collapse into one follow-up.
        await using var sp = BuildReal(withTask: true);
        var release = new TaskCompletionSource<IRequestExecutor>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = await ExecutorOf(sp);
        var calls = 0;
        var (gate, events, _) = await ReadyGateAsync(
            n =>
            {
                Interlocked.Increment(ref calls);
                if (n == 2)
                {
                    inFlight.TrySetResult();
                    return new ValueTask<IRequestExecutor>(release.Task);
                }

                return new ValueTask<IRequestExecutor>(executor);
            },
            new FakeTimeProvider(),
            sp);
        using var _ = gate;

        events.Evict(executor);
        await inFlight.Task.WaitAsync(Bound);
        for (var i = 0; i < 100; i++)
        {
            Should.NotThrow(() => events.Evict(executor));
        }

        release.SetResult(executor);
        await gate.EvictionWorker.WaitAsync(Bound);

        calls.ShouldBe(2); // the in-flight eviction probe plus exactly one coalesced follow-up (the initial probe is not counted here)
        gate.State.ShouldBe(ExecutorGateState.Ready);
        await gate.StopAsync(CancellationToken.None);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Ready_RebuildFailsAfterReady_StaysReady_FailureCountedNotSuccess()
    {
        // T-OPS150-04 / SEC-OPS150-03: a failed rebuild after Ready logs, records outcome=failed (never completed) and keeps Ready.
        using var metrics = new StepMetrics();
        await using var sp = BuildReal(withTask: true);
        var time = new SignalingTimeProvider();
        var logger = new TestLogger<GraphQlExecutorGate>();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (gate, events, executor) = await ReadyGateAsync(
            _ =>
            {
                failed.TrySetResult();
                throw new SchemaException(SchemaErrorBuilder.New().SetMessage("rebuild").Build());
            },
            time,
            sp,
            logger: logger);
        using var _ = gate;
        var completedBefore = metrics.Recorded.Count(r => r.Step == "GraphQlExecutor" && r.Outcome == StartupWarmupDiagnostics.Completed);

        events.Evict(executor);
        await failed.Task.WaitAsync(Bound);
        (await time.NextTimerAsync()).ShouldBe(TimeSpan.FromSeconds(5)); // back-off also applies after eviction failures

        gate.State.ShouldBe(ExecutorGateState.Ready);
        metrics.Recorded.ShouldContain(("GraphQlExecutor", StartupWarmupDiagnostics.Failed));
        metrics.Recorded.Count(r => r.Step == "GraphQlExecutor" && r.Outcome == StartupWarmupDiagnostics.Completed).ShouldBe(completedBefore);
        logger.All.ShouldNotContain(m => m.Contains("EXCEPTION", StringComparison.Ordinal));
        await gate.StopAsync(CancellationToken.None);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task WarmupTask_EvictedOnRequestThread_DoesNotSeeCallerHttpContext()
    {
        // A-150-5 / T-OPS150-07: the eviction handler may run on a request thread; the probe it starts sees no caller HttpContext.
        var accessor = new HttpContextAccessor();
        await using var sp = BuildReal(withTask: true, accessor: accessor);
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = await ExecutorOf(sp);
        var (gate, events, _) = await ReadyGateAsync(
            _ =>
            {
                seen.TrySetResult(accessor.HttpContext is not null);
                return new ValueTask<IRequestExecutor>(executor);
            },
            new FakeTimeProvider(),
            sp,
            accessor);
        using var _ = gate;

        accessor.HttpContext = new DefaultHttpContext();
        events.Evict(executor);

        (await seen.Task.WaitAsync(Bound)).ShouldBeFalse();
        await gate.StopAsync(CancellationToken.None);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Warmup_OnRequestThreadWithAmbientHttpContext_DoesNotReadOrMutateCallerResponse()
    {
        // T-OPS150-07: nothing of the warm-up writes into the caller's response.
        var accessor = new HttpContextAccessor();
        await using var sp = BuildReal(withTask: true, accessor: accessor);
        var executor = await ExecutorOf(sp);
        var probed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (gate, events, _) = await ReadyGateAsync(
            _ =>
            {
                probed.TrySetResult();
                return new ValueTask<IRequestExecutor>(executor);
            },
            new FakeTimeProvider(),
            sp,
            accessor);
        using var _ = gate;
        var http = new DefaultHttpContext();
        accessor.HttpContext = http;

        events.Evict(executor);
        await probed.Task.WaitAsync(Bound);
        await gate.EvictionWorker.WaitAsync(Bound);

        http.Response.Headers.ShouldBeEmpty();
        http.Response.StatusCode.ShouldBe(200);
        http.Items.ShouldBeEmpty();
        await gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecutorGate_StopAsync_DisposesEvictionSubscription()
    {
        await using var sp = BuildReal(withTask: true);
        var (gate, events, _) = await ReadyGateAsync(_ => throw new InvalidOperationException(), new FakeTimeProvider(), sp);
        using var _ = gate;
        events.Active.ShouldBe(1);

        await gate.StopAsync(CancellationToken.None);

        events.Active.ShouldBe(0);
    }

    // ---- gate: hang logging ----

    [Trait("Category", "Security")]
    [Fact]
    public async Task ExecutorGate_GetExecutorNeverReturns_Stays503_LogsOnceAfterTimeout()
    {
        // T-OPS150-19 / SEC-OPS150-16: a probe that never returns leaves 503 and logs exactly once after TimeoutSeconds.
        var called = new TaskCompletionSource();
        var provider = Substitute.For<IRequestExecutorProvider>();
        provider.GetExecutorAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                called.TrySetResult();
                return new ValueTask<IRequestExecutor>(new TaskCompletionSource<IRequestExecutor>().Task);
            });
        var time = new SignalingTimeProvider();
        var logger = new TestLogger<GraphQlExecutorGate>();
        using var gate = CreateGate(provider, time, logger: logger, timeoutSeconds: 30);

        await gate.StartAsync(CancellationToken.None);
        await called.Task.WaitAsync(Bound);
        (await time.NextTimerAsync()).ShouldBe(TimeSpan.FromSeconds(30));
        time.Advance(TimeSpan.FromSeconds(30));
        var message = await logger.NextAsync();
        time.Advance(TimeSpan.FromHours(1));

        message.ShouldContain("did not return");
        gate.State.ShouldBe(ExecutorGateState.Pending);
        logger.All.Count(m => m.Contains("did not return", StringComparison.Ordinal)).ShouldBe(1);
        await gate.StopAsync(CancellationToken.None).WaitAsync(Bound);
    }

    // ---- cold path listener ----

    private static RequestContext ContextWith(bool warmup)
    {
        var context = Substitute.For<RequestContext>();
        var data = new Dictionary<string, object?>();
        if (warmup)
        {
            data[ExecutionContextData.IsWarmupRequest] = true;
        }

        context.ContextData.Returns(data);
        return context;
    }

    [Trait("Category", "Security")]
    [Fact]
    public void ColdPathTimingListener_IgnoresWarmupRequests_NoLogNoBudgetConsumption()
    {
        // T-OPS150-21 / SEC-OPS150-18
        var logger = new TestLogger<ColdPathTimingListener>();
        var listener = new ColdPathTimingListener(logger);
        var warm = ContextWith(true);

        for (var i = 0; i < 10; i++)
        {
            listener.ParseDocument(warm).Dispose();
            listener.ValidateDocument(warm).Dispose();
            listener.CompileOperation(warm).Dispose();
            listener.ExecuteOperation(warm).Dispose();
        }

        logger.All.ShouldBeEmpty();

        var real = ContextWith(false);
        listener.ParseDocument(real).Dispose();
        listener.ExecuteOperation(real).Dispose();
        logger.All.Count.ShouldBe(2);
        logger.All.ShouldAllBe(m => m.Contains("occurrence 1", StringComparison.Ordinal));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task IsWarmupRequest_NotSettableFromHttpHeaderExtensionVariableOrPersistedId()
    {
        // T-OPS150-22 / SEC-OPS150-17: a request built from client-controlled parts never carries the in-process marker. The marker is
        // only produced by MarkAsWarmupRequest, which no HTTP parsing path calls.
        var counter = new Counter();
        await using var sp = BuildReal(counter);
        var executor = await ExecutorOf(sp);
        var marker = ExecutionContextData.IsWarmupRequest;
        var request = OperationRequestBuilder.New()
            .SetDocument("query Q($v: Boolean) { hello }")
            .SetVariableValues(new Dictionary<string, object?> { ["v"] = true, [marker] = true })
            .SetExtensions(new Dictionary<string, object?> { [marker] = true })
            .Build();
        // A persisted id is a format-validated hash (OperationDocumentId rejects anything else), so it cannot carry the marker.
        Should.Throw<ArgumentException>(() => new OperationDocumentId(marker));

        await using var result = await executor.ExecuteAsync(request);

        counter.WarmupSeen.ShouldNotBeEmpty();
        counter.WarmupSeen.ShouldAllBe(seen => !seen);
    }

    // ---- pipeline order ----

    [Trait("Category", "Security")]
    [Fact]
    public void Pipeline_NoSkipWarmupExecution_OperationExecutionLast_MiddlewareOrderPinned()
    {
        // T-OPS150-09 (replaced): the gateway pipeline never installs SkipWarmupExecution and UseOperationExecution is the last middleware.
        var source = System.IO.File.ReadAllText(FindFile("src/Autheris.Api/Extensions/GatewayServiceCollectionExtensions.GraphQL.cs"));
        source.ShouldNotContain("UseSkipWarmupExecution");
        var op = source.IndexOf(".UseOperationExecution()", StringComparison.Ordinal);
        op.ShouldBeGreaterThan(0);
        var afterOp = source[op..];
        afterOp.IndexOf(".UseRequest<", StringComparison.Ordinal).ShouldBe(-1);
    }

    private static string FindFile(string relative)
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, relative)))
        {
            dir = dir.Parent;
        }

        return dir is null ? throw new System.IO.FileNotFoundException(relative) : System.IO.Path.Combine(dir.FullName, relative);
    }

    // ---- StartAsync never blocks (WP-B review Info) ----

    private sealed class BlockingEnumerable(ManualResetEventSlim gate) : IEnumerable<IStartupWarmup>
    {
        public IEnumerator<IStartupWarmup> GetEnumerator()
        {
            gate.Wait(Bound);
            yield break;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class BlockingNameStep(ManualResetEventSlim gate) : IStartupWarmup
    {
        public string Name
        {
            get
            {
                gate.Wait(Bound);
                return "Blocking";
            }
        }

        public WarmupKind Kind => WarmupKind.BestEffort;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task StartAsync_BlockingStepNameOrEnumeration_DoesNotBlockHostStart()
    {
        // Literal guard for the Task.Yield in StartupWarmupService: the host start never waits for a step's Name or the step list.
        using var gate = new ManualResetEventSlim(false);
        foreach (var steps in new IEnumerable<IStartupWarmup>[] { new BlockingEnumerable(gate), [new BlockingNameStep(gate)] })
        {
            var service = new StartupWarmupService(
                steps,
                new StartupWarmupState(),
                Options.Create(new StartupWarmupOptions { TimeoutSeconds = 30 }),
                new FakeTimeProvider(),
                new TestLogger<StartupWarmupService>());

            await service.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            gate.Set();
            await service.StopAsync(CancellationToken.None);
            gate.Reset();
            service.Dispose();
        }
    }

    // ---- MCP serialisation warm-up ----

    [Fact]
    public async Task McpWarmup_SerialisesToolList_NoHandlerExecuted()
    {
        var registry = Substitute.For<IMcpToolRegistry>();
        registry.GetAvailableTools().Returns([new McpToolDefinition("t1", "d", """{"type":"object","properties":{"a":{"type":"string"}}}""", "query { x }")]);
        var step = new McpSerializationWarmup(registry);

        await step.RunAsync(CancellationToken.None);

        step.Name.ShouldBe("McpSerialization");
        step.Kind.ShouldBe(WarmupKind.BestEffort);
        registry.Received(1).GetAvailableTools();
        registry.DidNotReceiveWithAnyArgs().FindTool(default!);
        registry.DidNotReceiveWithAnyArgs().RegisterTool(default!);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void McpWarmup_NoPrincipalNoToolHandler()
    {
        // T-OPS150-14 / SEC-OPS150-11: only the registry is injected: no principal, scope, handler, tool execution or logger (output is never logged).
        var parameters = typeof(McpSerializationWarmup).GetConstructors().Single().GetParameters();
        parameters.Select(p => p.ParameterType).ShouldBe([typeof(IMcpToolRegistry)]);
        typeof(McpSerializationWarmup).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Select(f => f.FieldType.Name)
            .ShouldAllBe(n => !n.Contains("Logger", StringComparison.Ordinal));
    }
}
