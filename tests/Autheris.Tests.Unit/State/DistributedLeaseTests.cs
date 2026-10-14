namespace Autheris.Tests.Unit.State;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

/// <summary>A <see cref="TimeProvider"/> whose timers can be frozen to simulate a stalled process (GC pause, VM freeze).</summary>
internal sealed class PausableTimerTimeProvider(FakeTimeProvider inner) : TimeProvider
{
    public bool Paused { get; set; }

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

    public override long GetTimestamp() => inner.GetTimestamp();

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        inner.CreateTimer(s =>
        {
            if (!Paused)
            {
                callback(s);
            }
        }, state, dueTime, period);
}

public sealed class DistributedLeaseTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private sealed class FailingExtendLease : DistributedLeaseBase
    {
        public FailingExtendLease(TimeProvider time, TimeSpan ttl)
            : base(time, ttl, "failing", 1, NullLogger.Instance) => StartHeartbeat();

        public int ReleaseCalls { get; private set; }

        protected override ValueTask<bool> TryExtendAsync(CancellationToken ct) =>
            throw new ClusterStateUnavailableException("lease-extend", "failing", null);

        protected override ValueTask ReleaseAsync()
        {
            ReleaseCalls++;
            throw new ClusterStateUnavailableException("lease-release", "failing", null);
        }
    }

    [Fact]
    public async Task Lease_IsRenewed_WhileJobRuns_LongerThanInitialTtl()
    {
        var time = new FakeTimeProvider();
        var provider = new InMemoryClusterStateProvider(time);

        await using var lease = await provider.TryAcquireLeaseAsync("job", Ttl);
        lease.ShouldNotBeNull();

        for (var i = 0; i < 12; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
        }

        lease.LeaseLost.IsCancellationRequested.ShouldBeFalse();
        (await provider.TryAcquireLeaseAsync("job", Ttl)).ShouldBeNull();
        lease.LastConfirmedAt.ShouldBeGreaterThan(time.GetUtcNow() - Ttl);
    }

    [Fact]
    public async Task SecondAcquire_WhileHeld_ReturnsNull()
    {
        var provider = new InMemoryClusterStateProvider(new FakeTimeProvider());

        await using var first = await provider.TryAcquireLeaseAsync("job", Ttl);
        var second = await provider.TryAcquireLeaseAsync("job", Ttl);

        first.ShouldNotBeNull();
        second.ShouldBeNull();
    }

    [Fact]
    public async Task Token_IsStrictlyIncreasing_AcrossAcquisitions()
    {
        var provider = new InMemoryClusterStateProvider(new FakeTimeProvider());

        var first = await provider.TryAcquireLeaseAsync("job", Ttl);
        var firstToken = first!.Token;
        await first.DisposeAsync();
        var second = await provider.TryAcquireLeaseAsync("job", Ttl);
        var secondToken = second!.Token;
        await second.DisposeAsync();
        var third = await provider.TryAcquireLeaseAsync("job", Ttl);
        await using var _ = third;

        secondToken.ShouldBeGreaterThan(firstToken);
        third!.Token.ShouldBeGreaterThan(secondToken);
    }

    [Fact]
    public async Task Lease_NotRenewed_ExpiresAndOtherOwnerAcquires_FirstOwnerLeaseLostFires()
    {
        var fake = new FakeTimeProvider();
        var time = new PausableTimerTimeProvider(fake);
        var provider = new InMemoryClusterStateProvider(time);

        await using var first = await provider.TryAcquireLeaseAsync("job", Ttl);
        time.Paused = true; // the first owner stalls and misses every heartbeat
        fake.Advance(Ttl + TimeSpan.FromSeconds(1));

        await using var second = await provider.TryAcquireLeaseAsync("job", Ttl);
        second.ShouldNotBeNull();
        second.Token.ShouldBeGreaterThan(first!.Token);

        time.Paused = false;
        fake.Advance(TimeSpan.FromSeconds(10)); // the stalled owner wakes up: its extend must be rejected

        first.LeaseLost.IsCancellationRequested.ShouldBeTrue();
        second.LeaseLost.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task RenewFailures_BeyondTwoThirdsTtl_CancelLeaseLost()
    {
        var time = new FakeTimeProvider();
        await using var lease = new FailingExtendLease(time, Ttl);

        time.Advance(TimeSpan.FromSeconds(10)); // 1/3 ttl: still within the safety margin
        lease.LeaseLost.IsCancellationRequested.ShouldBeFalse();

        time.Advance(TimeSpan.FromSeconds(10)); // 2/3 ttl without confirmation: fail closed
        lease.LeaseLost.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AndNeverThrows()
    {
        var time = new FakeTimeProvider();
        var lease = new FailingExtendLease(time, Ttl);

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        lease.ReleaseCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Release_ByStaleOwner_DoesNotDeleteNewOwnersLease()
    {
        var fake = new FakeTimeProvider();
        var time = new PausableTimerTimeProvider(fake);
        var provider = new InMemoryClusterStateProvider(time);

        var stale = await provider.TryAcquireLeaseAsync("job", Ttl);
        time.Paused = true;
        fake.Advance(Ttl + TimeSpan.FromSeconds(1));
        await using var current = await provider.TryAcquireLeaseAsync("job", Ttl);
        current.ShouldNotBeNull();

        await stale!.DisposeAsync();

        (await provider.TryAcquireLeaseAsync("job", Ttl)).ShouldBeNull();
    }
}
