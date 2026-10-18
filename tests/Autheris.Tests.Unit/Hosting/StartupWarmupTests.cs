namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Hosting.Warmup;
using Autheris.Domain.Options;
using HotChocolate;
using HotChocolate.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>PLAN-OPS-150 WP-B: warm-up framework, options and the executor gate probe.</summary>
public sealed class StartupWarmupTests
{
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Includes the exception text if one were attached: the service must never attach it.
            Messages.Add(formatter(state, exception) + (exception is null ? string.Empty : "|EXCEPTION:" + exception));
        }
    }

    private sealed class FakeStep(string name, Func<CancellationToken, Task> run) : IStartupWarmup
    {
        public int Runs;

        public string Name => name;

        public WarmupKind Kind => WarmupKind.BestEffort;

        public Task RunAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Runs);
            return run(cancellationToken);
        }
    }

    private static StartupWarmupService CreateService(
        IEnumerable<IStartupWarmup> steps,
        StartupWarmupState state,
        FakeTimeProvider time,
        ILogger<StartupWarmupService>? logger = null,
        StartupWarmupOptions? options = null) =>
        new(steps, state, Options.Create(options ?? new StartupWarmupOptions { TimeoutSeconds = 30 }), time, logger ?? new CapturingLogger<StartupWarmupService>());

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "condition not reached in time");
            await Task.Delay(5);
        }
    }

    // The probe registers its back-off timer after publishing the state, so advance in small steps until the effect shows.
    private static async Task AdvanceUntilAsync(FakeTimeProvider time, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "condition not reached in time");
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Warmup_RunsStepsSequentially_ThenReady()
    {
        var state = new StartupWarmupState();
        var order = new List<string>();
        var a = new FakeStep("A", _ => { order.Add("A"); return Task.CompletedTask; });
        var b = new FakeStep("B", _ => { order.Add("B"); return Task.CompletedTask; });
        using var service = CreateService([a, b], state, new FakeTimeProvider());

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.Phase == WarmupPhase.Ready);

        order.ShouldBe(["A", "B"]);
        await service.StopAsync(CancellationToken.None);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task StartupWarmupService_YieldsBeforeFirstStep()
    {
        // T-OPS150-11 / SEC-OPS150-08: StartAsync returns before any step ran, even for a step that blocks the calling thread.
        var state = new StartupWarmupState();
        using var release = new ManualResetEventSlim();
        var step = new FakeStep("Blocking", _ => { release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None); return Task.CompletedTask; });
        using var service = CreateService([step], state, new FakeTimeProvider());

        await service.StartAsync(CancellationToken.None);

        state.Phase.ShouldBe(WarmupPhase.Pending);
        step.Runs.ShouldBe(0);
        release.Set();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Ready_StepNeverCompletes_Returns200AfterTimeout_LogsWarning()
    {
        var state = new StartupWarmupState();
        var logger = new CapturingLogger<StartupWarmupService>();
        var tcs = new TaskCompletionSource();
        var step = new FakeStep("Hung", ct => tcs.Task.WaitAsync(ct));
        var time = new FakeTimeProvider();
        using var service = CreateService([step], state, time, logger, new StartupWarmupOptions { TimeoutSeconds = 30 });

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.Phase == WarmupPhase.Warming && step.Runs == 1);
        state.Phase.ShouldBe(WarmupPhase.Warming);

        time.Advance(TimeSpan.FromSeconds(31));
        await WaitUntilAsync(() => state.Phase == WarmupPhase.Ready);

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
        await WaitUntilAsync(() => state.Phase == WarmupPhase.Ready);

        next.Runs.ShouldBe(1);
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
        var time = new FakeTimeProvider();
        using var service = CreateService([step], state, time, options: new StartupWarmupOptions { TimeoutSeconds = 5 });

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => step.Runs == 1);
        time.Advance(TimeSpan.FromSeconds(6));
        await WaitUntilAsync(() => state.Phase == WarmupPhase.Ready);

        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
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
        await WaitUntilAsync(() => state.Phase == WarmupPhase.Ready);

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
        await WaitUntilAsync(() => state.Phase == WarmupPhase.Ready);

        step.Runs.ShouldBeLessThanOrEqualTo(1);
        tcs.SetResult();
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
        await WaitUntilAsync(() => state.Phase == WarmupPhase.Ready);
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
        await WaitUntilAsync(() => state.Phase == WarmupPhase.Ready);
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
        var ok = new FakeStep("Okay", _ => Task.CompletedTask);
        var bad = new FakeStep("Bad", _ => throw new InvalidOperationException());
        using var service = CreateService([ok, bad], state, new FakeTimeProvider());

        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => state.Phase == WarmupPhase.Ready);
        await service.StopAsync(CancellationToken.None);

        lock (lockObj)
        {
            recorded.Select(r => r.Instrument).Distinct().Order().ShouldBe(["autheris.startup.warmup.duration", "autheris.startup.warmup.step.duration"]);
            recorded.Where(r => r.Instrument.EndsWith("step.duration", StringComparison.Ordinal))
                .Select(r => $"{r.Tags["step"]}={r.Tags["outcome"]}").Order().ShouldBe(["Bad=failed", "Okay=completed"]);
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
    public void WarmupDisabled_ReadyImmediately_NoHostedService()
    {
        using var sp = BuildWithConfig(new() { ["Gateway:Startup:Warmup:Enabled"] = "false" });

        sp.GetServices<IHostedService>().Any(h => h is StartupWarmupService).ShouldBeFalse();
        sp.GetRequiredService<IStartupWarmupState>().Phase.ShouldBe(WarmupPhase.Ready);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("false", "true")]
    [InlineData("true", "false")]
    public void Ready_WarmupDisabledOrNotRequired_ExecutorGateStillEnforced(string enabled, string required)
    {
        // T-OPS150-01 / SEC-OPS150-01: the gate is registered unconditionally and starts Pending (503) whatever the switches say.
        using var sp = BuildWithConfig(new()
        {
            ["Gateway:Startup:Warmup:Enabled"] = enabled,
            ["Gateway:Startup:Warmup:RequireForReadiness"] = required,
        });

        sp.GetServices<IHostedService>().Any(h => h is GraphQlExecutorGate).ShouldBeTrue();
        sp.GetRequiredService<IGraphQlExecutorGate>().State.ShouldBe(ExecutorGateState.Pending);
    }

    [Fact]
    public void WarmupDefault_Enabled_StartsPendingWithHostedService()
    {
        using var sp = BuildWithConfig([]);

        sp.GetServices<IHostedService>().Any(h => h is StartupWarmupService).ShouldBeTrue();
        sp.GetRequiredService<IStartupWarmupState>().Phase.ShouldBe(WarmupPhase.Pending);
    }

    // ---- executor gate ----

    private static GraphQlExecutorGate CreateGate(IRequestExecutorProvider provider, FakeTimeProvider time, bool persistedOnly = false)
    {
        var options = new GatewayOptions { GraphQL = new GraphQLOptions { PersistedQueriesOnly = persistedOnly } };
        return new GraphQlExecutorGate(provider, Options.Create(options), time, new CapturingLogger<GraphQlExecutorGate>());
    }

    private static async Task<IRequestExecutorProvider> RealProviderAsync(ServiceProvider[] keep)
    {
        var services = new ServiceCollection();
        // Same shape as the gateway pipeline: no UseSkipWarmupExecution (A-150-8), so the constant document really executes.
        services.AddGraphQLServer()
            .AddQueryType(d => d.Name("Query").Field("hello").Type<HotChocolate.Types.StringType>().Resolve(_ => new ValueTask<object?>("world")))
            .UseExceptions()
            .UseTimeout()
            .UseDocumentCache()
            .UseDocumentParser()
            .UseDocumentValidation()
            .UseOperationCache()
            .UseOperationResolver()
            .UseOperationVariableCoercion()
            .UseOperationExecution();
        var sp = services.BuildServiceProvider();
        keep[0] = sp;
        var provider = sp.GetRequiredService<IRequestExecutorProvider>();
        await provider.GetExecutorAsync();
        return provider;
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Ready_UninitialisedGate_IsPending() =>
        CreateGate(Substitute.For<IRequestExecutorProvider>(), new FakeTimeProvider()).State.ShouldBe(ExecutorGateState.Pending);

    [Fact]
    public async Task ExecutorGate_RealExecutor_TypenameProven_Ready()
    {
        var keep = new ServiceProvider[1];
        var provider = await RealProviderAsync(keep);
        await using var sp = keep[0];
        using var gate = CreateGate(provider, new FakeTimeProvider());

        await gate.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => gate.State == ExecutorGateState.Ready);
        await gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecutorGate_PersistedQueriesOnly_BuildIsTheProof()
    {
        var keep = new ServiceProvider[1];
        var provider = await RealProviderAsync(keep);
        await using var sp = keep[0];
        using var gate = CreateGate(provider, new FakeTimeProvider(), persistedOnly: true);

        await gate.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => gate.State == ExecutorGateState.Ready);
        await gate.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecutorGate_ProbeThrows_StateFailed_RetriesWithBackoff()
    {
        var provider = Substitute.For<IRequestExecutorProvider>();
        var calls = 0;
        provider.GetExecutorAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<IRequestExecutor>>(_ => { Interlocked.Increment(ref calls); throw new SchemaException(SchemaErrorBuilder.New().SetMessage("boom").Build()); });
        var time = new FakeTimeProvider();
        using var gate = CreateGate(provider, time);

        await gate.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => gate.State == ExecutorGateState.Failed && Volatile.Read(ref calls) == 1);

        // Far past the global warm-up budget: still Failed (the timeout never opens the gate) and retried with back-off.
        await AdvanceUntilAsync(time, () => Volatile.Read(ref calls) == 2);
        await AdvanceUntilAsync(time, () => Volatile.Read(ref calls) == 3);
        time.Advance(TimeSpan.FromSeconds(500));
        gate.State.ShouldBe(ExecutorGateState.Failed);
        await gate.StopAsync(CancellationToken.None);
    }
}
