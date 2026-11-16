namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Endpoints.Health;
using Autheris.Api.Hosting.Warmup;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

[Trait("Category", "Security")]
public sealed class ReadinessProbeTests
{
    private sealed class FakeHealthCheckService(
        Func<CancellationToken, Task<GatewayHealthReport>> handler) : IGatewayHealthCheckService
    {
        private int _calls;
        private readonly TaskCompletionSource _firstCallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Completes when the first underlying probe has started (the probe runs on the thread pool).</summary>
        public Task FirstCallStarted => _firstCallStarted.Task;

        public CancellationToken LastToken { get; private set; }

        public Task<GatewayHealthReport> CheckHealthAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            LastToken = ct;
            _firstCallStarted.TrySetResult();
            return handler(ct);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public sealed record Entry(LogLevel Level, string Message, Exception? Exception);

        private readonly object _gate = new();
        private readonly List<Entry> _entries = [];

        public IReadOnlyList<Entry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _entries.Add(new Entry(logLevel, formatter(state, exception), exception));
            }
        }
    }

    private sealed class FakeDrainController(bool draining = false) : ITrafficDrainController
    {
        public bool IsDraining { get; set; } = draining;

        public int ActiveQueryCount => 0;

        public void InitiateGracefulShutdown() => IsDraining = true;

        public void MarkCompleted()
        {
        }

        public IDisposable TrackQuery() => NoopDisposable.Instance;

        public Task WaitForCompletionAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeExecutorGate(ExecutorGateState state) : IGraphQlExecutorGate
    {
        public ExecutorGateState State { get; set; } = state;
    }

    private sealed class FakeWarmupState(WarmupPhase phase) : IStartupWarmupState
    {
        public WarmupPhase Phase { get; set; } = phase;
    }

    public class RedisDispatchProxy : DispatchProxy
    {
        private Func<MethodInfo?, object?[]?, object?> _handler = null!;

        public static T Create<T>(Func<MethodInfo?, object?[]?, object?> handler) where T : class
        {
            var proxy = Create<T, RedisDispatchProxy>();
            ((RedisDispatchProxy)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _handler(targetMethod, args);
    }

    private static IHostEnvironment CreateEnvironment(string environmentName)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environmentName);
        return env;
    }

    private static async Task<(int StatusCode, string Body)> ExecuteReadyAsync(
        IHostEnvironment environment,
        GatewayOptions gatewayOptions,
        ITrafficDrainController drain,
        ReadinessProbe? probe,
        IGraphQlExecutorGate? gate,
        IStartupWarmupState? warmup,
        IDictionary<string, string>? headers = null)
    {
        var result = await HealthEndpoints.HandleReadyAsync(drain, probe, gate, warmup, gatewayOptions, environment, CancellationToken.None);
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider()
        };
        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                context.Request.Headers[k] = v;
            }
        }
        var bodyStream = new MemoryStream();
        context.Response.Body = bodyStream;
        await result.ExecuteAsync(context);
        bodyStream.Seek(0, SeekOrigin.Begin);
        var body = new StreamReader(bodyStream).ReadToEnd();
        return (context.Response.StatusCode, body);
    }

    [Fact]
    public void Constructor_NullArguments_ThrowArgumentNullException()
    {
        var health = new FakeHealthCheckService(_ => Task.FromResult(new GatewayHealthReport(true, [])));
        var logger = NullLogger<ReadinessProbe>.Instance;

        Should.Throw<ArgumentNullException>(() => new ReadinessProbe(null!, logger));
        Should.Throw<ArgumentNullException>(() => new ReadinessProbe(health, null!));
    }

    [Fact]
    public async Task Parallel_calls_within_window_run_one_probe()
    {
        var gate = new TaskCompletionSource<GatewayHealthReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var health = new FakeHealthCheckService(_ => gate.Task);
        var time = new FakeTimeProvider();
        var probe = new ReadinessProbe(health, NullLogger<ReadinessProbe>.Instance, time);

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => probe.CheckHealthAsync(CancellationToken.None))
            .ToArray();

        // Single-flight: only one underlying probe started while in-flight. The probe is started on the thread pool, so wait for it
        // deterministically instead of yielding; all 50 callers have already joined the in-flight probe (they were issued synchronously).
        await health.FirstCallStarted.WaitAsync(TimeSpan.FromSeconds(30));
        health.Calls.ShouldBe(1);

        var expected = new GatewayHealthReport(true, [new HealthCheckComponentResult("GovernanceDb", true, "ok")]);
        gate.SetResult(expected);
        var results = await Task.WhenAll(tasks);

        results.ShouldAllBe(r => r.IsHealthy);
        health.Calls.ShouldBe(1);

        // Subsequent sequential call inside the 2 s cache window reuses the cached report.
        time.Advance(TimeSpan.FromMilliseconds(1500));
        var cached = await probe.CheckHealthAsync(CancellationToken.None);
        cached.IsHealthy.ShouldBeTrue();
        health.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Probe_runs_again_after_window()
    {
        var time = new FakeTimeProvider();
        var health = new FakeHealthCheckService(_ => Task.FromResult(new GatewayHealthReport(true, [])));
        var probe = new ReadinessProbe(health, NullLogger<ReadinessProbe>.Instance, time);

        await probe.CheckHealthAsync(CancellationToken.None);
        health.Calls.ShouldBe(1);

        time.Advance(TimeSpan.FromMilliseconds(1999));
        await probe.CheckHealthAsync(CancellationToken.None);
        health.Calls.ShouldBe(1);

        time.Advance(TimeSpan.FromMilliseconds(1));
        await probe.CheckHealthAsync(CancellationToken.None);
        health.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_cancel_shared_probe()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<GatewayHealthReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var health = new FakeHealthCheckService(async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return await release.Task;
        });

        var time = new FakeTimeProvider();
        var probe = new ReadinessProbe(health, NullLogger<ReadinessProbe>.Instance, time);

        using var caller1Cts = new CancellationTokenSource();
        var caller1Task = probe.CheckHealthAsync(caller1Cts.Token);
        await entered.Task;

        var caller2Task = probe.CheckHealthAsync(CancellationToken.None);

        caller1Cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(caller1Task);

        health.LastToken.IsCancellationRequested.ShouldBeFalse();

        release.SetResult(new GatewayHealthReport(true, []));
        var caller2Report = await caller2Task;
        caller2Report.IsHealthy.ShouldBeTrue();
        health.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task PreCanceledToken_ThrowsOperationCanceledWithoutProbing()
    {
        var health = new FakeHealthCheckService(_ => Task.FromResult(new GatewayHealthReport(true, [])));
        var probe = new ReadinessProbe(health, NullLogger<ReadinessProbe>.Instance, new FakeTimeProvider());

        await Should.ThrowAsync<OperationCanceledException>(
            () => probe.CheckHealthAsync(new CancellationToken(canceled: true)));

        health.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Probe_exception_yields_unhealthy_and_is_not_sticky()
    {
        var time = new FakeTimeProvider();
        var logger = new RecordingLogger<ReadinessProbe>();
        var shouldThrow = true;
        var health = new FakeHealthCheckService(_ =>
        {
            if (shouldThrow)
            {
                throw new InvalidOperationException("Host=secret-db.internal;Password=top-secret-password");
            }

            return Task.FromResult(new GatewayHealthReport(true, [new HealthCheckComponentResult("GovernanceDb", true, "ok")]));
        });

        var probe = new ReadinessProbe(health, logger, time);

        var first = await probe.CheckHealthAsync(CancellationToken.None);
        var second = await probe.CheckHealthAsync(CancellationToken.None);

        first.IsHealthy.ShouldBeFalse();
        second.IsHealthy.ShouldBeFalse();
        first.Components.ShouldAllBe(c =>
            c.Description == null ||
            (!c.Description.Contains("secret-db.internal", StringComparison.OrdinalIgnoreCase) &&
             !c.Description.Contains("top-secret-password", StringComparison.OrdinalIgnoreCase)));

        // Cached for the 2 s window; exception logged once per probe run.
        health.Calls.ShouldBe(1);
        logger.Entries.Count(e => e.Level >= LogLevel.Warning).ShouldBe(1);

        // Not sticky after the 2 s cache window expires.
        shouldThrow = false;
        time.Advance(TimeSpan.FromSeconds(2));
        var recovered = await probe.CheckHealthAsync(CancellationToken.None);

        recovered.IsHealthy.ShouldBeTrue();
        health.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task UnhealthyReport_IsCachedForWindowAndRecoversAfterExpiry()
    {
        var time = new FakeTimeProvider();
        var healthy = false;
        var health = new FakeHealthCheckService(_ => Task.FromResult(new GatewayHealthReport(healthy, [new HealthCheckComponentResult("GovernanceDb", healthy)])));
        var probe = new ReadinessProbe(health, NullLogger<ReadinessProbe>.Instance, time);

        (await probe.CheckHealthAsync(CancellationToken.None)).IsHealthy.ShouldBeFalse();
        (await probe.CheckHealthAsync(CancellationToken.None)).IsHealthy.ShouldBeFalse();
        health.Calls.ShouldBe(1);

        healthy = true;
        time.Advance(TimeSpan.FromSeconds(2));
        (await probe.CheckHealthAsync(CancellationToken.None)).IsHealthy.ShouldBeTrue();
        health.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Draining_returns_503_without_probe()
    {
        var health = new FakeHealthCheckService(_ => Task.FromResult(new GatewayHealthReport(true, [])));
        var drain = new FakeDrainController(draining: true);
        var gate = new FakeExecutorGate(ExecutorGateState.Ready);
        var warmup = new FakeWarmupState(WarmupPhase.Ready);

        var (statusCode, body) = await ExecuteReadyAsync(CreateEnvironment("Production"), new GatewayOptions(), drain, new ReadinessProbe(health, NullLogger<ReadinessProbe>.Instance), gate, warmup);

        statusCode.ShouldBe((int)HttpStatusCode.ServiceUnavailable);
        body.ShouldBeEmpty();
        health.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Redis_ping_hang_is_bounded_by_probe_timeout()
    {
        var time = new FakeTimeProvider();
        var logger = new RecordingLogger<ReadinessProbe>();
        var pingCalls = 0;

        var hangingDb = RedisDispatchProxy.Create<IDatabase>((method, _) =>
        {
            if (method?.Name == nameof(IDatabase.PingAsync))
            {
                Interlocked.Increment(ref pingCalls);
                return new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            }

            throw new NotSupportedException(method?.Name);
        });

        var mux = RedisDispatchProxy.Create<IConnectionMultiplexer>((method, _) => method?.Name switch
        {
            $"get_{nameof(IConnectionMultiplexer.IsConnected)}" => true,
            nameof(IConnectionMultiplexer.GetDatabase) => hangingDb,
            _ => throw new NotSupportedException(method?.Name)
        });

        var gatewayOptions = new GatewayOptions
        {
            Caching = new CachingOptions
            {
                Redis = new RedisOptions { Enabled = true }
            }
        };

        var inner = new GatewayHealthCheckService(
            Options.Create(gatewayOptions),
            NullLogger<GatewayHealthCheckService>.Instance,
            redisMultiplexer: mux);

        var probe = new ReadinessProbe(inner, logger, time);

        var task = probe.CheckHealthAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(5));

        var report = await task.WaitAsync(TimeSpan.FromSeconds(5));

        report.IsHealthy.ShouldBeFalse();
        pingCalls.ShouldBe(1);

        // Timed-out result is cached for the 2 s window only.
        var cachedReport = await probe.CheckHealthAsync(CancellationToken.None);
        cachedReport.IsHealthy.ShouldBeFalse();
        pingCalls.ShouldBe(1);

        // After the 2 s window expires, a subsequent call attempts the probe again.
        time.Advance(TimeSpan.FromSeconds(2));
        var nextTask = probe.CheckHealthAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(5));
        var nextReport = await nextTask.WaitAsync(TimeSpan.FromSeconds(5));

        nextReport.IsHealthy.ShouldBeFalse();
        pingCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Cached_report_does_not_leak_into_development_detail_body()
    {
        var time = new FakeTimeProvider();
        var health = new FakeHealthCheckService(_ => Task.FromResult(
            new GatewayHealthReport(false, [new HealthCheckComponentResult("GovernanceDb", false, "down")])));

        var options = new GatewayOptions
        {
            Authentication = new AuthenticationOptions
            {
                EnableTestAuthHandler = true
            }
        };

        var sharedProbe = new ReadinessProbe(health, NullLogger<ReadinessProbe>.Instance, time);
        var drain = new FakeDrainController();
        var gate = new FakeExecutorGate(ExecutorGateState.Ready);
        var warmup = new FakeWarmupState(WarmupPhase.Ready);

        // Development call populates the cached dependency report and returns dev details.
        var (devStatus, devJson) = await ExecuteReadyAsync(CreateEnvironment("Development"), options, drain, sharedProbe, gate, warmup);
        devStatus.ShouldBe((int)HttpStatusCode.ServiceUnavailable);
        devJson.ShouldContain("securityMode");
        devJson.ShouldContain("components");

        // Production call hits the cached dependency report (Calls == 1) and never leaks dev fields.
        var (prodStatus, prodJson) = await ExecuteReadyAsync(CreateEnvironment("Production"), options, drain, sharedProbe, gate, warmup);
        prodStatus.ShouldBe((int)HttpStatusCode.ServiceUnavailable);
        using var doc = JsonDocument.Parse(prodJson);
        var props = doc.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        props.ShouldBe(["status", "timestamp"]);
        health.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Warmup_and_gate_states_are_read_live()
    {
        var time = new FakeTimeProvider();
        var health = new FakeHealthCheckService(_ => Task.FromResult(new GatewayHealthReport(true, [])));
        var drain = new FakeDrainController();
        var gate = new FakeExecutorGate(ExecutorGateState.Pending);
        var warmup = new FakeWarmupState(WarmupPhase.Warming);
        var probe = new ReadinessProbe(health, NullLogger<ReadinessProbe>.Instance, time);
        var env = CreateEnvironment("Production");
        var options = new GatewayOptions();

        var (s1, _) = await ExecuteReadyAsync(env, options, drain, probe, gate, warmup);
        s1.ShouldBe((int)HttpStatusCode.ServiceUnavailable);
        health.Calls.ShouldBe(1);

        gate.State = ExecutorGateState.Ready;
        var (s2, _) = await ExecuteReadyAsync(env, options, drain, probe, gate, warmup);
        s2.ShouldBe((int)HttpStatusCode.ServiceUnavailable);
        health.Calls.ShouldBe(1);

        warmup.Phase = WarmupPhase.Ready;
        var (s3, _) = await ExecuteReadyAsync(env, options, drain, probe, gate, warmup);
        s3.ShouldBe((int)HttpStatusCode.OK);
        health.Calls.ShouldBe(1);

        // Draining flips immediately to 503 even while healthy report is cached.
        drain.IsDraining = true;
        var (s4, _) = await ExecuteReadyAsync(env, options, drain, probe, gate, warmup);
        s4.ShouldBe((int)HttpStatusCode.ServiceUnavailable);
        health.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task RequestHeadersAndHosts_ShareSingletonProbeCache()
    {
        var time = new FakeTimeProvider();
        var health = new FakeHealthCheckService(_ => Task.FromResult(new GatewayHealthReport(true, [])));
        var drain = new FakeDrainController();
        var gate = new FakeExecutorGate(ExecutorGateState.Ready);
        var warmup = new FakeWarmupState(WarmupPhase.Ready);
        var probe = new ReadinessProbe(health, NullLogger<ReadinessProbe>.Instance, time);
        var env = CreateEnvironment("Production");
        var options = new GatewayOptions();

        var (s1, _) = await ExecuteReadyAsync(env, options, drain, probe, gate, warmup, new Dictionary<string, string> { ["Host"] = "tenant-a.example.com", ["X-Tenant-Id"] = "tenant-a" });
        var (s2, _) = await ExecuteReadyAsync(env, options, drain, probe, gate, warmup, new Dictionary<string, string> { ["Host"] = "tenant-b.example.com", ["X-Tenant-Id"] = "tenant-b" });

        s1.ShouldBe((int)HttpStatusCode.OK);
        s2.ShouldBe((int)HttpStatusCode.OK);
        health.Calls.ShouldBe(1);
    }
}
