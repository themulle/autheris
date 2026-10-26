namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Hosting.Warmup;
using Autheris.Api.Middleware;
using Autheris.Domain.Options;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>PLAN-OPS-150 WP-B: warm-up framework, options and the executor gate probe. No real-time polling: signals and a fake clock only.</summary>
public sealed class StartupWarmupTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            // Includes the exception text if one were attached: the code under test must never attach it.
            Messages.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : "|EXCEPTION:" + exception));
    }

    /// <summary>Fake clock that signals every timer registration, so tests advance time only once the code waits on it.</summary>
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

    private sealed class FakeStep(string name, Func<CancellationToken, Task> run) : IStartupWarmup
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Runs;

        public Task Started => _started.Task.WaitAsync(Bound);

        public string Name => name;

        public WarmupKind Kind => WarmupKind.BestEffort;

        public Task RunAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Runs);
            _started.TrySetResult();
            return run(cancellationToken);
        }
    }

    private sealed class ThrowingNameStep : IStartupWarmup
    {
        public string Name => throw new InvalidOperationException("name lookup blew up");

        public WarmupKind Kind => WarmupKind.BestEffort;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static StartupWarmupService CreateService(
        IEnumerable<IStartupWarmup> steps,
        StartupWarmupState state,
        FakeTimeProvider time,
        ILogger<StartupWarmupService>? logger = null,
        StartupWarmupOptions? options = null) =>
        new(steps, state, Options.Create(options ?? new StartupWarmupOptions { TimeoutSeconds = 30 }), time, logger ?? new CapturingLogger<StartupWarmupService>());

    private static Task Finished(BackgroundService service) => service.ExecuteTask!.WaitAsync(Bound);

    [Fact]
    public async Task Warmup_RunsStepsSequentially_ThenReady()
    {
        var state = new StartupWarmupState();
        var order = new ConcurrentQueue<string>();
        var a = new FakeStep("A", _ => { order.Enqueue("A"); return Task.CompletedTask; });
        var b = new FakeStep("B", _ => { order.Enqueue("B"); return Task.CompletedTask; });
        using var service = CreateService([a, b], state, new FakeTimeProvider());

        await service.StartAsync(CancellationToken.None);
        await Finished(service);

        state.Phase.ShouldBe(WarmupPhase.Ready);
        order.ToArray().ShouldBe(["A", "B"]);
        await service.StopAsync(CancellationToken.None);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task StartupWarmupService_YieldsBeforeFirstStep()
    {
        // T-OPS150-11 / SEC-OPS150-08: StartAsync returns before any step ran, even for a step that blocks the calling thread.
        var state = new StartupWarmupState();
        using var release = new ManualResetEventSlim();
        var step = new FakeStep("Blocking", _ => { release.Wait(Bound, CancellationToken.None); return Task.CompletedTask; });
        using var service = CreateService([step], state, new FakeTimeProvider());

        // The blocking step would hold StartAsync forever if ExecuteAsync ran steps inline: it must return while the step is still blocked.
        await service.StartAsync(CancellationToken.None).WaitAsync(Bound);
        release.IsSet.ShouldBeFalse();

        await step.Started;
        release.Set();
        await Finished(service);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Ready_StepNeverCompletes_Returns200AfterTimeout_LogsWarning()
    {
        var state = new StartupWarmupState();
        var logger = new CapturingLogger<StartupWarmupService>();
        var tcs = new TaskCompletionSource();
        var step = new FakeStep("Hung", ct => tcs.Task.WaitAsync(ct));
        var time = new SignalingTimeProvider();
        using var service = CreateService([step], state, time, logger, new StartupWarmupOptions { TimeoutSeconds = 30 });

        await service.StartAsync(CancellationToken.None);
        await step.Started;
        await time.NextTimerAsync();
        await time.NextTimerAsync();
        state.Phase.ShouldBe(WarmupPhase.Warming);

        time.Advance(TimeSpan.FromSeconds(31));
        await Finished(service);

        state.Phase.ShouldBe(WarmupPhase.Ready);
        logger.Messages.ShouldContain(m => m.Contains("Hung") && m.Contains("budget"));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Ready_ThrowingBestEffortStep_OtherStepsRun_HostStarts()
    {
        var state = new StartupWarmupState();
        var throwing = new FakeStep("Boom", _ => throw new InvalidOperationException("boom"));
        var next = new FakeStep("Next", _ => Task.CompletedTask);
        using var service = CreateService([throwing, next], state, new FakeTimeProvider());

        await service.StartAsync(CancellationToken.None);
        await Finished(service);

        state.Phase.ShouldBe(WarmupPhase.Ready);
        next.Runs.ShouldBe(1);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Warmup_StepNameThrows_HostSurvives_OtherStepsRun()
    {
        var state = new StartupWarmupState();
        var next = new FakeStep("Next", _ => Task.CompletedTask);
        using var service = CreateService([new ThrowingNameStep(), next], state, new FakeTimeProvider());

        await service.StartAsync(CancellationToken.None);
        await Finished(service);

        service.ExecuteTask!.IsCompletedSuccessfully.ShouldBeTrue();
        state.Phase.ShouldBe(WarmupPhase.Ready);
        next.Runs.ShouldBe(1);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Warmup_StepOwnCancellation_IsFailedNotTimedOut()
    {
        var state = new StartupWarmupState();
        var logger = new CapturingLogger<StartupWarmupService>();
        var step = new FakeStep("SelfCancel", _ => throw new OperationCanceledException());
        using var service = CreateService([step], state, new FakeTimeProvider(), logger);

        await service.StartAsync(CancellationToken.None);
        await Finished(service);

        logger.Messages.ShouldContain(m => m.Contains("SelfCancel") && m.Contains("failed") && m.Contains(nameof(OperationCanceledException)));
        logger.Messages.ShouldNotContain(m => m.Contains("budget"));
        await service.StopAsync(CancellationToken.None);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Warmup_NonCooperativeStep_ReleasedByHardBound_HostStartsAndStops()
    {
        // T-OPS150-11 / SEC-OPS150-08: a step that ignores its token (and blocks a thread) is released by the hard bound.
        var state = new StartupWarmupState();
        using var neverSet = new ManualResetEventSlim();
        var step = new FakeStep("Stubborn", _ => { neverSet.Wait(TimeSpan.FromSeconds(60), CancellationToken.None); return Task.CompletedTask; });
        var time = new SignalingTimeProvider();
        using var service = CreateService([step], state, time, options: new StartupWarmupOptions { TimeoutSeconds = 5 });

        await service.StartAsync(CancellationToken.None);
        await step.Started;
        await time.NextTimerAsync();
        await time.NextTimerAsync();
        time.Advance(TimeSpan.FromSeconds(6));
        await Finished(service);

        state.Phase.ShouldBe(WarmupPhase.Ready);
        await service.StopAsync(CancellationToken.None).WaitAsync(Bound);
        neverSet.Set();
    }

    [Fact]
    public async Task Warmup_StepDisabledByOption_IsSkipped()
    {
        var state = new StartupWarmupState();
        var step = new FakeStep("McpSerialization", _ => Task.CompletedTask);
        var options = new StartupWarmupOptions { Steps = { ["McpSerialization"] = new StartupWarmupStepOptions { Enabled = false } } };
        using var service = CreateService([step], state, new FakeTimeProvider(), options: options);

        await service.StartAsync(CancellationToken.None);
        await Finished(service);

        step.Runs.ShouldBe(0);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Warmup_RequireForReadinessFalse_ReadyWhileStepStillRuns()
    {
        var state = new StartupWarmupState();
        var tcs = new TaskCompletionSource();
        var step = new FakeStep("Slow", ct => tcs.Task.WaitAsync(ct));
        using var service = CreateService([step], state, new FakeTimeProvider(), options: new StartupWarmupOptions { RequireForReadiness = false });

        await service.StartAsync(CancellationToken.None);
        await step.Started;

        state.Phase.ShouldBe(WarmupPhase.Ready);
        tcs.SetResult();
        await Finished(service);
        await service.StopAsync(CancellationToken.None);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task WarmupLog_NoExceptionMessageNoQueryTextNoKeyMaterial()
    {
        // T-OPS150-12 / SEC-OPS150-09
        var state = new StartupWarmupState();
        var logger = new CapturingLogger<StartupWarmupService>();
        var step = new FakeStep("Leaky", _ => throw new InvalidOperationException("query { secret } key=AKIA-PRIVATE-KEY token=abc"));
        using var service = CreateService([step], state, new FakeTimeProvider(), logger);

        await service.StartAsync(CancellationToken.None);
        await Finished(service);
        await service.StopAsync(CancellationToken.None);

        var text = string.Join('\n', logger.Messages);
        text.ShouldContain("Leaky");
        text.ShouldContain(nameof(InvalidOperationException));
        text.ShouldNotContain("secret");
        text.ShouldNotContain("AKIA");
        text.ShouldNotContain("token=");
        text.ShouldNotContain("EXCEPTION:");
    }

    [Fact]
    public async Task WarmupLog_ContainsStepNameAndExceptionTypeOnly()
    {
        var state = new StartupWarmupState();
        var logger = new CapturingLogger<StartupWarmupService>();
        var step = new FakeStep("Named", _ => throw new TimeoutException("detail-text"));
        using var service = CreateService([step], state, new FakeTimeProvider(), logger);

        await service.StartAsync(CancellationToken.None);
        await Finished(service);
        await service.StopAsync(CancellationToken.None);

        logger.Messages.ShouldContain(m => m.Contains("Named") && m.Contains(nameof(TimeoutException)) && !m.Contains("detail-text"));
    }

    [Fact]
    public async Task WarmupMetrics_RecordDurationAndOutcome_NoRequestCounters()
    {
        var recorded = new List<(string Instrument, double Value, Dictionary<string, object?> Tags)>();
        var lockObj = new object();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == "Autheris.Metrics" && inst.Name.StartsWith("autheris.startup.warmup", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(inst);
            }
        };
        listener.SetMeasurementEventCallback<double>((inst, value, tags, _) =>
        {
            lock (lockObj)
            {
                recorded.Add((inst.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
            }
        });
        listener.Start();

        var state = new StartupWarmupState();
        // The meter is process-global: other warm-up tests run in parallel, so this test records only its own uniquely named steps.
        var id = Guid.NewGuid().ToString("N");
        var okName = $"Okay-{id}";
        var badName = $"Bad-{id}";
        var ok = new FakeStep(okName, _ => Task.CompletedTask);
        var bad = new FakeStep(badName, _ => throw new InvalidOperationException());
        using var service = CreateService([ok, bad], state, new FakeTimeProvider());

        await service.StartAsync(CancellationToken.None);
        await Finished(service);
        await service.StopAsync(CancellationToken.None);

        lock (lockObj)
        {
            var instruments = recorded.Select(r => r.Instrument).Distinct().ToList();
            instruments.ShouldContain("autheris.startup.warmup.duration");
            instruments.ShouldContain("autheris.startup.warmup.step.duration");
            recorded.Where(r => r.Instrument.EndsWith("step.duration", StringComparison.Ordinal) && (Equals(r.Tags["step"], okName) || Equals(r.Tags["step"], badName)))
                .Select(r => $"{r.Tags["step"]}={r.Tags["outcome"]}").Order().ShouldBe([$"{badName}=failed", $"{okName}=completed"]);
            recorded.All(r => r.Tags.Keys.All(k => k == "step" || k == "outcome")).ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    [InlineData(-5)]
    public void WarmupOptions_TimeoutOutOfRange_RejectsStart(int seconds)
    {
        var result = new StartupWarmupOptionsValidator().Validate(null, new StartupWarmupOptions { TimeoutSeconds = seconds });

        result.Failed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(300)]
    public void WarmupOptions_TimeoutInRange_Passes(int seconds) =>
        new StartupWarmupOptionsValidator().Validate(null, new StartupWarmupOptions { TimeoutSeconds = seconds }).Succeeded.ShouldBeTrue();

    [Theory]
    [InlineData("Nonsense")]
    [InlineData("GraphQlExecutor")]
    [InlineData("graphqlexecutor")]
    public void WarmupOptions_UnknownOrExecutorStepName_Fails(string step)
    {
        var options = new StartupWarmupOptions { Steps = { [step] = new StartupWarmupStepOptions() } };

        var result = new StartupWarmupOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldNotContain(step, Case.Sensitive);
    }

    [Fact]
    public void WarmupOptions_McpSerializationStep_Passes() =>
        new StartupWarmupOptionsValidator().Validate(null, new StartupWarmupOptions { Steps = { ["McpSerialization"] = new StartupWarmupStepOptions() } })
            .Succeeded.ShouldBeTrue();

    private static ServiceProvider BuildWithConfig(Dictionary<string, string?> values)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");
        services.AddSingleton(env);
        services.AddOptions<GatewayOptions>();
        services.AddSingleton(Substitute.For<IRequestExecutorProvider>());
        GatewayStartupWarmupServiceExtensions.CaptureColdPathTiming(services, config);
        services.AddAutherisStartupWarmup();
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("Gateway:Startup:Warmup:TimeoutSeconds", "0")]
    [InlineData("Gateway:Startup:Warmup:TimeoutSeconds", "301")]
    [InlineData("Gateway:Startup:Warmup:Steps:Unknown:Enabled", "true")]
    [InlineData("Gateway:Startup:Warmup:Steps:GraphQlExecutor:Enabled", "true")]
    public void WarmupOptions_InvalidConfiguration_RejectsHostStart(string key, string value)
    {
        using var sp = BuildWithConfig(new() { [key] = value });

        var ex = Should.Throw<Exception>(sp.GetRequiredService<IStartupValidator>().Validate);
        (ex is OptionsValidationException || ex is AggregateException { InnerExceptions: [OptionsValidationException, ..] }).ShouldBeTrue();
    }

    [Fact]
    public async Task WarmupDisabled_ReadyImmediately_NoHostedService()
    {
        await using var sp = BuildWithConfig(new() { ["Gateway:Startup:Warmup:Enabled"] = "false" });

        sp.GetServices<IHostedService>().Any(h => h is StartupWarmupService).ShouldBeFalse();
        sp.GetRequiredService<IStartupWarmupState>().Phase.ShouldBe(WarmupPhase.Ready);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("false", "true")]
    [InlineData("true", "false")]
    public async Task Ready_WarmupDisabledOrNotRequired_ExecutorGateStillEnforced(string enabled, string required)
    {
        // T-OPS150-01 / SEC-OPS150-01: the gate is registered unconditionally and starts Pending (503) whatever the switches say.
        await using var sp = BuildWithConfig(new()
        {
            ["Gateway:Startup:Warmup:Enabled"] = enabled,
            ["Gateway:Startup:Warmup:RequireForReadiness"] = required,
        });

        sp.GetServices<IHostedService>().Any(h => h is GraphQlExecutorGate).ShouldBeTrue();
        sp.GetRequiredService<IGraphQlExecutorGate>().State.ShouldBe(ExecutorGateState.Pending);
    }

    [Fact]
    public async Task WarmupDefault_Enabled_StartsPendingWithHostedService()
    {
        await using var sp = BuildWithConfig([]);

        sp.GetServices<IHostedService>().Any(h => h is StartupWarmupService).ShouldBeTrue();
        sp.GetRequiredService<IStartupWarmupState>().Phase.ShouldBe(WarmupPhase.Pending);
    }

    // ---- executor gate ----

    private static GraphQlExecutorGate CreateGate(IRequestExecutorProvider provider, FakeTimeProvider time, bool persistedOnly = false)
    {
        var options = new GatewayOptions { GraphQL = new GraphQLOptions { PersistedQueriesOnly = persistedOnly } };
        return new GraphQlExecutorGate(provider, Options.Create(options), time, new CapturingLogger<GraphQlExecutorGate>());
    }

    /// <summary>A real executor with the gateway pipeline shape (no UseSkipWarmupExecution, A-150-8), optionally with the trusted-document middleware.</summary>
    private static async Task<(ServiceProvider Services, IRequestExecutorProvider Provider)> RealProviderAsync(TrustedDocumentStore? store = null, string queryName = "Query")
    {
        var services = new ServiceCollection();
        var builder = services.AddGraphQLServer()
            .AddQueryType(d => d.Name(queryName).Field("hello").Type<HotChocolate.Types.StringType>().Resolve(_ => new ValueTask<object?>("world")))
            .UseExceptions()
            .UseTimeout()
            .UseDocumentCache();
        if (store is not null)
        {
            services.AddSingleton(store);
            builder.UseRequest<TrustedDocumentsOnlyMiddleware>();
        }

        builder.UseDocumentParser()
            .UseDocumentValidation()
            .UseOperationCache()
            .UseOperationResolver()
            .UseOperationVariableCoercion()
            .UseOperationExecution();
        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<IRequestExecutorProvider>();
        await provider.GetExecutorAsync();
        return (sp, provider);
    }

    private static IRequestExecutorProvider ProviderThatThrows(SemaphoreSlim calls)
    {
        var provider = Substitute.For<IRequestExecutorProvider>();
        provider.GetExecutorAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<IRequestExecutor>>(_ =>
            {
                calls.Release();
                throw new SchemaException(SchemaErrorBuilder.New().SetMessage("boom").Build());
            });
        return provider;
    }

    private static IRequestExecutorProvider ProviderWithExecutorResult(IExecutionResult result)
    {
        var executor = Substitute.For<IRequestExecutor>();
        executor.ExecuteAsync(Arg.Any<IOperationRequest>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(result));
        var provider = Substitute.For<IRequestExecutorProvider>();
        provider.GetExecutorAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<IRequestExecutor>(executor));
        return provider;
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Ready_UninitialisedGate_IsPending() =>
        CreateGate(Substitute.For<IRequestExecutorProvider>(), new FakeTimeProvider()).State.ShouldBe(ExecutorGateState.Pending);

    [Fact]
    public async Task ExecutorGate_RealExecutor_TypenameProven_Ready()
    {
        var (sp, provider) = await RealProviderAsync();
        await using var _ = sp;
        using var gate = CreateGate(provider, new FakeTimeProvider());

        await gate.StartAsync(CancellationToken.None);
        await Finished(gate);

        gate.State.ShouldBe(ExecutorGateState.Ready);
        await gate.StopAsync(CancellationToken.None);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ExecutorGate_PersistedQueriesOnly_TrustedDocumentRejection_IsProof_StoreUnchanged()
    {
        // T-OPS150-10 / SEC-OPS150-07: the rejection of the constant by the trusted-document middleware proves the executor ran its
        // pipeline; the warm-up document is never added to the store.
        var store = new TrustedDocumentStore(["{ hello }"]);
        var (sp, provider) = await RealProviderAsync(store);
        await using var _ = sp;
        using var gate = CreateGate(provider, new FakeTimeProvider(), persistedOnly: true);

        await gate.StartAsync(CancellationToken.None);
        await Finished(gate);

        gate.State.ShouldBe(ExecutorGateState.Ready);
        store.Count.ShouldBe(1);
        store.IsTrusted(GraphQlExecutorGate.WarmupDocument).ShouldBeFalse();
        await gate.StopAsync(CancellationToken.None);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ExecutorGate_PersistedQueriesOnly_OtherError_StaysPending()
    {
        // SEC-OPS150-01: only exactly one TRUSTED_DOCUMENT_REQUIRED error counts; any other outcome is not proof.
        var other = OperationResult.FromError(ErrorBuilder.New().SetMessage("x").SetCode("SOMETHING_ELSE").Build());
        var time = new SignalingTimeProvider();
        using var gate = CreateGate(ProviderWithExecutorResult(other), time, persistedOnly: true);

        await gate.StartAsync(CancellationToken.None);
        await time.NextTimerAsync(); // the back-off after the unproven probe

        gate.State.ShouldBe(ExecutorGateState.Pending);
        await gate.StopAsync(CancellationToken.None);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ExecutorGate_PersistedQueriesOnly_RejectionWithExtraError_StaysPending()
    {
        var two = OperationResult.FromError(
            ErrorBuilder.New().SetMessage("a").SetCode(TrustedDocumentsOnlyMiddleware.RejectionCode).Build(),
            ErrorBuilder.New().SetMessage("b").SetCode("SOMETHING_ELSE").Build());
        var time = new SignalingTimeProvider();
        using var gate = CreateGate(ProviderWithExecutorResult(two), time, persistedOnly: true);

        await gate.StartAsync(CancellationToken.None);
        await time.NextTimerAsync();

        gate.State.ShouldBe(ExecutorGateState.Pending);
        await gate.StopAsync(CancellationToken.None);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ExecutorGate_NotPersistedOnly_TrustedDocumentRejection_StaysPending()
    {
        // The rejection code is proof only when PersistedQueriesOnly is configured.
        var rejection = OperationResult.FromError(ErrorBuilder.New().SetMessage("x").SetCode(TrustedDocumentsOnlyMiddleware.RejectionCode).Build());
        var time = new SignalingTimeProvider();
        using var gate = CreateGate(ProviderWithExecutorResult(rejection), time, persistedOnly: false);

        await gate.StartAsync(CancellationToken.None);
        await time.NextTimerAsync();

        gate.State.ShouldBe(ExecutorGateState.Pending);
        await gate.StopAsync(CancellationToken.None);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ExecutorGate_ExecutorBuiltButTypenameNotProven_StaysPending()
    {
        // SEC-OPS150-01 (A-150-7): an executor was returned but the constant did not yield data.__typename == "Query".
        var (sp, provider) = await RealProviderAsync(queryName: "NotQuery");
        await using var _ = sp;
        var time = new SignalingTimeProvider();
        using var gate = CreateGate(provider, time);

        await gate.StartAsync(CancellationToken.None);
        await time.NextTimerAsync();

        gate.State.ShouldBe(ExecutorGateState.Pending);
        await gate.StopAsync(CancellationToken.None);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ExecutorGate_GetExecutorNeverReturns_Stays503_PastTimeout()
    {
        // T-OPS150-19 / SEC-OPS150-16 / D-14: a hung probe leaves the gate Pending however much time passes.
        var called = new TaskCompletionSource();
        var provider = Substitute.For<IRequestExecutorProvider>();
        provider.GetExecutorAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                called.TrySetResult();
                return new ValueTask<IRequestExecutor>(new TaskCompletionSource<IRequestExecutor>().Task);
            });
        var time = new FakeTimeProvider();
        using var gate = CreateGate(provider, time);

        await gate.StartAsync(CancellationToken.None);
        await called.Task.WaitAsync(Bound);
        time.Advance(TimeSpan.FromMinutes(10));

        gate.State.ShouldBe(ExecutorGateState.Pending);
        gate.ExecuteTask!.IsCompleted.ShouldBeFalse();
        await gate.StopAsync(CancellationToken.None).WaitAsync(Bound);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ExecutorGate_ProbeThrows_StateFailed_RetriesWithExactBackoff()
    {
        // A-150-1 / SEC-OPS150-15: 5 s doubling, flat 60 s cap, retried indefinitely; the warm-up timeout never opens it.
        var calls = new SemaphoreSlim(0);
        var time = new SignalingTimeProvider();
        using var gate = CreateGate(ProviderThatThrows(calls), time);

        await gate.StartAsync(CancellationToken.None);
        (await calls.WaitAsync(Bound)).ShouldBeTrue();
        (await time.NextTimerAsync()).ShouldBe(TimeSpan.FromSeconds(5));
        gate.State.ShouldBe(ExecutorGateState.Failed);

        time.Advance(TimeSpan.FromSeconds(4.9));
        calls.CurrentCount.ShouldBe(0);
        time.Advance(TimeSpan.FromSeconds(0.1));
        (await calls.WaitAsync(Bound)).ShouldBeTrue();

        foreach (var expected in new[] { 10, 20, 40, 60, 60, 60 })
        {
            (await time.NextTimerAsync()).ShouldBe(TimeSpan.FromSeconds(expected));
            time.Advance(TimeSpan.FromSeconds(expected));
            (await calls.WaitAsync(Bound)).ShouldBeTrue();
            gate.State.ShouldBe(ExecutorGateState.Failed);
        }

        await gate.StopAsync(CancellationToken.None);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ExecutorGate_ReadyLatched_LaterFailingProviderLeavesItReady()
    {
        // SEC-OPS150-03: once Ready it latches; it neither re-probes nor flips back.
        var (sp, real) = await RealProviderAsync();
        await using var _ = sp;
        var executor = await real.GetExecutorAsync();
        var calls = 0;
        var provider = Substitute.For<IRequestExecutorProvider>();
        provider.GetExecutorAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            return Interlocked.Increment(ref calls) == 1
                ? new ValueTask<IRequestExecutor>(executor)
                : throw new SchemaException(SchemaErrorBuilder.New().SetMessage("later").Build());
        });
        var time = new FakeTimeProvider();
        using var gate = CreateGate(provider, time);

        await gate.StartAsync(CancellationToken.None);
        await Finished(gate);
        time.Advance(TimeSpan.FromHours(1));

        gate.State.ShouldBe(ExecutorGateState.Ready);
        calls.ShouldBe(1);
        await gate.StopAsync(CancellationToken.None);
    }

    private static readonly AsyncLocal<string?> Ambient = new();

    [Trait("Category", "Security")]
    [Fact]
    public async Task ExecutorGate_ProbeRunsWithoutAmbientIdentity_NoAsyncLocalNoHttpContext()
    {
        // T-OPS150-07 / SEC-OPS150-05: neither an AsyncLocal nor IHttpContextAccessor.HttpContext set by the starting request flows into the probe.
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        Ambient.Value = "caller-principal";
        var seen = new TaskCompletionSource<(string? Local, HttpContext? Http)>();
        var provider = Substitute.For<IRequestExecutorProvider>();
        provider.GetExecutorAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns<ValueTask<IRequestExecutor>>(_ =>
        {
            seen.TrySetResult((Ambient.Value, accessor.HttpContext));
            throw new InvalidOperationException("stop here");
        });
        var time = new FakeTimeProvider();
        using var gate = CreateGate(provider, time);

        await gate.StartAsync(CancellationToken.None);
        var (local, http) = await seen.Task.WaitAsync(Bound);

        local.ShouldBeNull();
        http.ShouldBeNull();
        await gate.StopAsync(CancellationToken.None);
        Ambient.Value = null;
    }
}
