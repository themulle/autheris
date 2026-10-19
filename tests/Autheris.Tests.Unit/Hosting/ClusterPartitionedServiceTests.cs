namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Hosting;
using Autheris.Application.State;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>REQ-CDC-99 D-A2: one fenced lease loop per partition (a tracked table).</summary>
public sealed class ClusterPartitionedServiceTests
{
    private static readonly ClusterSchedule Schedule = new(true, TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.FromSeconds(15), TimeSpan.Zero);

    private sealed class TestService(IDistributedClusterStateProvider state, TimeProvider time) : ClusterPartitionedService(state, NullLogger.Instance, time)
    {
        public List<ClusterPartition> Wanted { get; set; } = [];

        public bool ThrowOnRead { get; set; }

        public ConcurrentQueue<(string Key, long Token)> Runs { get; } = new();

        public Func<ClusterPartition, ClusterJobContext, CancellationToken, Task>? Behaviour { get; set; }

        protected override IReadOnlyList<ClusterPartition> GetPartitions() =>
            ThrowOnRead ? throw new InvalidOperationException("options broken") : Wanted;

        protected override async Task ExecutePartitionAsync(ClusterPartition partition, ClusterJobContext context, CancellationToken ct)
        {
            Runs.Enqueue((partition.Key, context.FencingToken));
            if (Behaviour is { } b)
            {
                await b(partition, context, ct);
            }
        }
    }

    private sealed class FakeLease(string key, long token) : IDistributedLease
    {
        private readonly CancellationTokenSource _lost = new();

        public bool Disposed { get; private set; }

        public string ResourceKey => key;

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

    private static ClusterPartition P(string key) => new(key, Schedule);

    private static (TestService Service, IDistributedClusterStateProvider State, FakeTimeProvider Time) Create()
    {
        var state = Substitute.For<IDistributedClusterStateProvider>();
        var time = new FakeTimeProvider();
        return (new TestService(state, time), state, time);
    }

    private static void GrantLeases(IDistributedClusterStateProvider state, ConcurrentDictionary<string, FakeLease> leases, long token = 7) =>
        state.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var lease = new FakeLease(call.ArgAt<string>(0), token);
                leases[lease.ResourceKey] = lease;
                return new ValueTask<IDistributedLease?>(lease);
            });

    [Fact]
    public async Task Cycle_AcquiresALeasePerPartitionKey_AndPassesItsFencingToken()
    {
        var (service, state, _) = Create();
        var leases = new ConcurrentDictionary<string, FakeLease>();
        GrantLeases(state, leases, token: 42);

        (await service.RunPartitionCycleAsync(P("cdc:a"), CancellationToken.None)).ShouldBeTrue();
        (await service.RunPartitionCycleAsync(P("cdc:b"), CancellationToken.None)).ShouldBeTrue();

        await state.Received(1).TryAcquireLeaseAsync("cdc:a", Schedule.LeaseTtl, Arg.Any<CancellationToken>());
        await state.Received(1).TryAcquireLeaseAsync("cdc:b", Schedule.LeaseTtl, Arg.Any<CancellationToken>());
        service.Runs.ToArray().ShouldBe([("cdc:a", 42L), ("cdc:b", 42L)]);
        leases.Values.ShouldAllBe(l => l.Disposed);
    }

    [Fact]
    public async Task Cycle_LeaseHeldByAnotherReplica_DoesNotRun()
    {
        var (service, state, _) = Create();
        state.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IDistributedLease?>((IDistributedLease?)null));

        (await service.RunPartitionCycleAsync(P("cdc:a"), CancellationToken.None)).ShouldBeFalse();

        service.Runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cycle_ClusterStateUnavailable_SkipsInsteadOfRunningUnguarded()
    {
        var (service, state, _) = Create();
        state.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<IDistributedLease?>>(_ => throw new ClusterStateUnavailableException("lease", "cdc:a", null));

        (await service.RunPartitionCycleAsync(P("cdc:a"), CancellationToken.None)).ShouldBeFalse();

        service.Runs.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cycle_LeaseLost_CancelsTheRunningPartitionWork()
    {
        var (service, state, _) = Create();
        var leases = new ConcurrentDictionary<string, FakeLease>();
        GrantLeases(state, leases);
        var started = new TaskCompletionSource();
        service.Behaviour = async (_, _, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };

        var cycle = service.RunPartitionCycleAsync(P("cdc:a"), CancellationToken.None);
        await started.Task;
        leases["cdc:a"].Lose();

        (await cycle).ShouldBeFalse();
        leases["cdc:a"].Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Cycle_FencedOut_IsContainedAndReported()
    {
        var (service, state, _) = Create();
        GrantLeases(state, new ConcurrentDictionary<string, FakeLease>());
        service.Behaviour = (_, _, _) => throw new FencingTokenRejectedException("cdc:a", 7);

        (await service.RunPartitionCycleAsync(P("cdc:a"), CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task Cycle_PartitionFailure_DoesNotAffectAnotherPartition()
    {
        var (service, state, _) = Create();
        GrantLeases(state, new ConcurrentDictionary<string, FakeLease>());
        service.Behaviour = (p, _, _) => p.Key == "cdc:bad" ? throw new InvalidOperationException("boom") : Task.CompletedTask;

        (await service.RunPartitionCycleAsync(P("cdc:bad"), CancellationToken.None)).ShouldBeFalse();
        (await service.RunPartitionCycleAsync(P("cdc:good"), CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task Cycle_HostShutdown_Propagates()
    {
        var (service, state, _) = Create();
        GrantLeases(state, new ConcurrentDictionary<string, FakeLease>());
        using var cts = new CancellationTokenSource();
        service.Behaviour = async (_, _, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        };

        await Should.ThrowAsync<OperationCanceledException>(() => service.RunPartitionCycleAsync(P("cdc:a"), cts.Token));
    }

    [Fact]
    public async Task Reconcile_StartsOneLoopPerPartition_AndStopsRemovedOnes()
    {
        var (service, state, _) = Create();
        state.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IDistributedLease?>((IDistributedLease?)null));
        service.Wanted = [P("cdc:a"), P("cdc:b"), P("cdc:a")];

        await service.ReconcileAsync();
        service.ActivePartitionKeys.Order().ToArray().ShouldBe(["cdc:a", "cdc:b"]);

        service.Wanted = [P("cdc:b"), P("cdc:c")];
        await service.ReconcileAsync();
        service.ActivePartitionKeys.Order().ToArray().ShouldBe(["cdc:b", "cdc:c"]);

        service.Wanted = [];
        await service.ReconcileAsync();
        service.ActivePartitionKeys.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reconcile_FailingRead_KeepsThePreviousPartitions()
    {
        var (service, state, _) = Create();
        state.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IDistributedLease?>((IDistributedLease?)null));
        service.Wanted = [P("cdc:a")];
        await service.ReconcileAsync();

        service.ThrowOnRead = true;
        await service.ReconcileAsync();

        service.ActivePartitionKeys.ToArray().ShouldBe(["cdc:a"]);
        service.Wanted = [];
        service.ThrowOnRead = false;
        await service.ReconcileAsync();
    }

    [Fact]
    public async Task StartedLoops_RunTheirFirstCycleImmediately_EachUnderItsOwnLease()
    {
        var (service, state, _) = Create();
        GrantLeases(state, new ConcurrentDictionary<string, FakeLease>());
        service.Wanted = [P("cdc:a"), P("cdc:b")];

        await service.ReconcileAsync();

        (await Eventually.WaitAsync(() => service.Runs.Count >= 2, TimeSpan.FromSeconds(5))).ShouldBeTrue();
        service.Runs.Select(r => r.Key).Order().ToArray().ShouldBe(["cdc:a", "cdc:b"]);
        service.Wanted = [];
        await service.ReconcileAsync();
    }

    [Fact]
    public async Task DisabledPartition_NeverRuns()
    {
        var (service, state, _) = Create();
        GrantLeases(state, new ConcurrentDictionary<string, FakeLease>());
        service.Wanted = [new ClusterPartition("cdc:a", Schedule with { Enabled = false })];

        await service.ReconcileAsync();
        await Task.Yield();

        service.Runs.ShouldBeEmpty();
        service.Wanted = [];
        await service.ReconcileAsync();
    }
}
