namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Hosting;
using Autheris.Application.State;
using Autheris.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

public sealed class ClusterScheduledServiceTests
{
    private sealed class Probe
    {
        public int Runs;
        public long LastToken;
        public CancellationToken LastCancellation;
        public Func<CancellationToken, Task>? Behaviour;
        public Action? OnRun;
    }

    private sealed class ProbeJob(Probe probe) : IClusterScheduledJob
    {
        public async Task ExecuteAsync(ClusterJobContext context, CancellationToken ct)
        {
            probe.OnRun?.Invoke();
            probe.LastToken = context.FencingToken;
            probe.LastCancellation = ct;
            Interlocked.Increment(ref probe.Runs);
            if (probe.Behaviour is { } behaviour)
            {
                await behaviour(ct);
            }
        }
    }

    private sealed class MutableSchedule
    {
        public ClusterSchedule Value { get; set; } = new(true, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2), TimeSpan.Zero);
    }

    private sealed class TestService(
        IServiceScopeFactory scopes,
        IDistributedClusterStateProvider state,
        MutableSchedule schedule,
        TimeProvider time)
        : ClusterScheduledService<ProbeJob>(scopes, state, NullLogger<TestService>.Instance, time)
    {
        protected override string LeaseKey => "test:job";

        protected override ClusterSchedule GetSchedule() => schedule.Value;
    }

    private sealed class FakeLease(long token) : IDistributedLease
    {
        private readonly CancellationTokenSource _lost = new();

        public bool Disposed { get; private set; }

        public string ResourceKey => "test:job";

        public long Token => token;

        public CancellationToken LeaseLost => _lost.Token;

        public DateTimeOffset LastConfirmedAt => DateTimeOffset.UtcNow;

        public void Lose() => _lost.Cancel();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public FakeTimeProvider Time { get; } = new();

        public Probe Probe { get; } = new();

        public MutableSchedule Schedule { get; } = new();

        public IDistributedClusterStateProvider State { get; } = Substitute.For<IDistributedClusterStateProvider>();

        public TestService Service { get; }

        private readonly ServiceProvider _provider;

        public Harness()
        {
            var services = new ServiceCollection();
            services.AddSingleton(Probe);
            services.AddScoped<ProbeJob>();
            _provider = services.BuildServiceProvider();
            Service = new TestService(_provider.GetRequiredService<IServiceScopeFactory>(), State, Schedule, Time);
        }

        public async Task StartAsync() => await Service.StartAsync(CancellationToken.None);

        public async Task AdvanceAsync(TimeSpan by)
        {
            // Advance in small steps so cycles started by an earlier timer can schedule the next one.
            var remaining = by;
            var step = TimeSpan.FromSeconds(1);
            while (remaining > TimeSpan.Zero)
            {
                var slice = remaining < step ? remaining : step;
                Time.Advance(slice);
                remaining -= slice;
                await Task.Delay(5);
            }
        }

        public async Task<bool> WaitForRunsAsync(int runs) =>
            await Eventually.WaitAsync(() => Volatile.Read(ref Probe.Runs) >= runs, TimeSpan.FromSeconds(5));

        public async ValueTask DisposeAsync()
        {
            await Service.StopAsync(CancellationToken.None);
            Service.Dispose();
            await _provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task AcquireThrows_DoesNotStopHost_AndJobNotRun()
    {
        await using var h = new Harness();
        var attempts = 0;
        h.State.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<IDistributedLease?>>(_ =>
            {
                Interlocked.Increment(ref attempts);
                throw new ClusterStateUnavailableException("lease", "test:job", null);
            });

        await h.StartAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(6));
        (await Eventually.WaitAsync(() => Volatile.Read(ref attempts) >= 1, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));
        (await Eventually.WaitAsync(() => Volatile.Read(ref attempts) >= 2, TimeSpan.FromSeconds(5))).ShouldBeTrue();

        h.Probe.Runs.ShouldBe(0);
        h.Service.ExecuteTask!.IsFaulted.ShouldBeFalse();
        h.Service.ExecuteTask.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task LockBusy_SkipsJob()
    {
        await using var h = new Harness();
        var attempts = 0;
        h.State.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref attempts);
                return new ValueTask<IDistributedLease?>((IDistributedLease?)null);
            });

        await h.StartAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(6));
        (await Eventually.WaitAsync(() => Volatile.Read(ref attempts) >= 1, TimeSpan.FromSeconds(5))).ShouldBeTrue();

        h.Probe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task SuccessfulCycle_PassesFencingToken_AndReleasesLease()
    {
        await using var h = new Harness();
        var lease = new FakeLease(42);
        h.State.TryAcquireLeaseAsync("test:job", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IDistributedLease?>(lease));

        await h.StartAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(6));

        (await h.WaitForRunsAsync(1)).ShouldBeTrue();
        h.Probe.LastToken.ShouldBe(42);
        (await Eventually.WaitAsync(() => lease.Disposed, TimeSpan.FromSeconds(5))).ShouldBeTrue();
    }

    [Fact]
    public async Task LeaseLost_CancelsJobToken()
    {
        await using var h = new Harness();
        var lease = new FakeLease(1);
        h.State.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IDistributedLease?>(lease));
        h.Probe.Behaviour = ct => Task.Delay(Timeout.Infinite, ct);

        await h.StartAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(6));
        (await h.WaitForRunsAsync(1)).ShouldBeTrue();
        h.Probe.LastCancellation.IsCancellationRequested.ShouldBeFalse();

        lease.Lose();

        (await Eventually.WaitAsync(() => h.Probe.LastCancellation.IsCancellationRequested, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        (await Eventually.WaitAsync(() => lease.Disposed, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        h.Service.ExecuteTask!.IsCompleted.ShouldBeFalse();
    }

    [Fact]
    public async Task JobThrows_LoopContinues()
    {
        await using var h = new Harness();
        h.State.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<IDistributedLease?>(new FakeLease(1)));
        h.Probe.Behaviour = _ => throw new InvalidOperationException("boom");

        await h.StartAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(6));
        (await h.WaitForRunsAsync(1)).ShouldBeTrue();
        await h.AdvanceAsync(TimeSpan.FromMinutes(1));

        (await h.WaitForRunsAsync(2)).ShouldBeTrue();
        h.Service.ExecuteTask!.IsFaulted.ShouldBeFalse();
    }

    [Fact]
    public async Task IntervalChange_ViaOptionsMonitor_Applies()
    {
        await using var h = new Harness();
        h.State.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<IDistributedLease?>(new FakeLease(1)));
        h.Probe.OnRun = () =>
        {
            if (Volatile.Read(ref h.Probe.Runs) == 0)
            {
                h.Schedule.Value = h.Schedule.Value with { Interval = TimeSpan.FromMinutes(10) };
            }
        };

        await h.StartAsync();
        await h.AdvanceAsync(TimeSpan.FromSeconds(6));
        (await h.WaitForRunsAsync(1)).ShouldBeTrue();

        await h.AdvanceAsync(TimeSpan.FromMinutes(2));
        h.Probe.Runs.ShouldBe(1); // would be 2 with the old one-minute interval

        await h.AdvanceAsync(TimeSpan.FromMinutes(9));
        (await h.WaitForRunsAsync(2)).ShouldBeTrue();
    }

    [Fact]
    public async Task DisabledSchedule_NeverAcquires()
    {
        await using var h = new Harness();
        h.Schedule.Value = h.Schedule.Value with { Enabled = false };

        await h.StartAsync();
        await h.AdvanceAsync(TimeSpan.FromMinutes(2));

        await h.State.DidNotReceive().TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Constructor_NullClusterState_Throws()
    {
        var scopes = Substitute.For<IServiceScopeFactory>();

        var act = () => new TestService(scopes, null!, new MutableSchedule(), TimeProvider.System);

        Should.Throw<ArgumentNullException>(act);
    }
}
