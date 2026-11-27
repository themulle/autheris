namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Exceptions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class OperationReadSlotsTests
{
    private const string Key = "t|u|orders";

    private static (TableReadConcurrencyGate Gate, OperationReadSlots Slots, FakeTimeProvider Time) Create(int max)
    {
        var time = new FakeTimeProvider();
        var gate = new TableReadConcurrencyGate(time);
        return (gate, new OperationReadSlots(gate, Key, max, time), time);
    }

    [Fact]
    public async Task SixRootsLimitFour_ZeroWait_AllSucceed_PeakFour()
    {
        var (gate, slots, _) = Create(4);
        var inUse = 0;
        var peak = 0;
        var done = 0;
        var holders = new List<TaskCompletionSource>();

        async Task Root(TaskCompletionSource finish)
        {
            using var lease = await slots.AcquireAsync(TimeSpan.Zero, CancellationToken.None);
            peak = Math.Max(peak, Interlocked.Increment(ref inUse));
            await finish.Task;
            Interlocked.Decrement(ref inUse);
            Interlocked.Increment(ref done);
        }

        var tasks = new List<Task>();
        for (var i = 0; i < 6; i++)
        {
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            holders.Add(finish);
            tasks.Add(Root(finish));
        }

        foreach (var finish in holders)
        {
            finish.SetResult();
            await Task.Yield();
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        done.ShouldBe(6);
        peak.ShouldBeLessThanOrEqualTo(4);
        gate.TryEnter(Key, 4).ShouldNotBeNull();
    }

    [Fact]
    public async Task SiblingWaiter_ReceivesHandedOverSlot()
    {
        var (gate, slots, _) = Create(1);
        var first = await slots.AcquireAsync(TimeSpan.Zero, CancellationToken.None);
        var second = slots.AcquireAsync(TimeSpan.Zero, CancellationToken.None).AsTask();
        second.IsCompleted.ShouldBeFalse();

        first.Dispose();

        var lease = await second;
        gate.TryEnter(Key, 1).ShouldBeNull();
        lease.Dispose();
        lease.Dispose();
        gate.TryEnter(Key, 1).ShouldNotBeNull();
    }

    [Fact]
    public async Task NoSiblingHoldsSlot_OtherRequestsFull_ThrowsThrottledImmediatelyWhenWaitZero()
    {
        var (gate, slots, _) = Create(1);
        using var other = gate.TryEnter(Key, 1);

        var pending = slots.AcquireAsync(TimeSpan.Zero, CancellationToken.None);

        pending.IsCompleted.ShouldBeTrue();
        await Should.ThrowAsync<GatewayThrottledException>(async () => await pending);
    }

    [Fact]
    public async Task NoSiblingHoldsSlot_OtherRequestFreesWithinWait_Succeeds()
    {
        var (gate, slots, _) = Create(1);
        var other = gate.TryEnter(Key, 1)!;

        var pending = slots.AcquireAsync(TimeSpan.FromSeconds(2), CancellationToken.None).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        other.Dispose();

        (await pending).Dispose();
        gate.TryEnter(Key, 1).ShouldNotBeNull();
    }

    [Fact]
    public async Task CancelledSiblingWaiter_ReleasesNothingAndLeaksNoSlot()
    {
        var (gate, slots, _) = Create(1);
        var first = await slots.AcquireAsync(TimeSpan.Zero, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var second = slots.AcquireAsync(TimeSpan.Zero, cts.Token).AsTask();

        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => second);
        gate.TryEnter(Key, 1).ShouldBeNull();
        first.Dispose();

        gate.TryEnter(Key, 1).ShouldNotBeNull();
    }

    [Fact]
    public async Task Dispose_FailsPendingWaitersThrottled_AndLaterReleaseFreesGate()
    {
        var (gate, slots, _) = Create(1);
        var first = await slots.AcquireAsync(TimeSpan.Zero, CancellationToken.None);
        var second = slots.AcquireAsync(TimeSpan.Zero, CancellationToken.None).AsTask();

        slots.Dispose();

        await Should.ThrowAsync<GatewayThrottledException>(() => second);
        await Should.ThrowAsync<GatewayThrottledException>(async () => await slots.AcquireAsync(TimeSpan.Zero, CancellationToken.None));
        first.Dispose();
        gate.TryEnter(Key, 1).ShouldNotBeNull();
    }

    [Fact]
    public async Task AllRootsDone_GateCountBackToZero()
    {
        var (gate, slots, _) = Create(2);
        var leases = new[]
        {
            await slots.AcquireAsync(TimeSpan.Zero, CancellationToken.None),
            await slots.AcquireAsync(TimeSpan.Zero, CancellationToken.None),
        };
        var waiting = slots.AcquireAsync(TimeSpan.Zero, CancellationToken.None).AsTask();

        leases[0].Dispose();
        var third = await waiting;
        leases[1].Dispose();
        third.Dispose();

        gate.TryEnter(Key, 2).ShouldNotBeNull();
        gate.TryEnter(Key, 2).ShouldNotBeNull();
        gate.TryEnter(Key, 2).ShouldBeNull();
    }

    [Fact]
    public async Task SiblingWait_EndsAtFixedUpperLimit()
    {
        var (gate, slots, time) = Create(1);
        var first = await slots.AcquireAsync(TimeSpan.Zero, CancellationToken.None);
        var second = slots.AcquireAsync(TimeSpan.Zero, CancellationToken.None).AsTask();

        time.Advance(OperationReadSlots.SiblingWaitLimit);

        await Should.ThrowAsync<GatewayThrottledException>(() => second);
        first.Dispose();
        gate.TryEnter(Key, 1).ShouldNotBeNull();
    }

    [Fact]
    public async Task SiblingWaiters_BeyondCap_AreThrottledImmediately()
    {
        var (_, slots, _) = Create(1);
        var first = await slots.AcquireAsync(TimeSpan.Zero, CancellationToken.None);
        var waiters = Enumerable.Range(0, OperationReadSlots.MaxSiblingWaiters)
            .Select(_ => slots.AcquireAsync(TimeSpan.Zero, CancellationToken.None).AsTask())
            .ToList();

        var overflow = slots.AcquireAsync(TimeSpan.Zero, CancellationToken.None);

        overflow.IsCompleted.ShouldBeTrue();
        await Should.ThrowAsync<GatewayThrottledException>(async () => await overflow);
        first.Dispose();
        foreach (var waiter in waiters)
        {
            (await waiter).Dispose();
        }
    }
}
