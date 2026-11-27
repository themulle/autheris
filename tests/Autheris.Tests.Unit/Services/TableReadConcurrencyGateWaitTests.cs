namespace Autheris.Tests.Unit.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Quota;
using Autheris.Application.Services;
using Autheris.Application.State.Caching.Services;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class TableReadConcurrencyGateWaitTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    private static string[] K(params string[] keys) => keys;

    [Fact]
    public async Task EnterAsync_SlotFreedWithinWait_GrantsLease()
    {
        var gate = new TableReadConcurrencyGate(new FakeTimeProvider());
        var held = gate.TryEnter("k", 1)!;

        var pending = gate.EnterAsync(K("k"), 1, Wait, CancellationToken.None).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        held.Dispose();

        var lease = await pending;
        lease.ShouldNotBeNull();
        gate.TryEnter("k", 1).ShouldBeNull();
        lease.Dispose();
        gate.TryEnter("k", 1).ShouldNotBeNull();
    }

    [Fact]
    public async Task EnterAsync_WaitExpires_ReturnsNull()
    {
        var time = new FakeTimeProvider();
        var gate = new TableReadConcurrencyGate(time);
        using var held = gate.TryEnter("k", 1);

        var pending = gate.EnterAsync(K("k"), 1, Wait, CancellationToken.None).AsTask();
        time.Advance(Wait);

        (await pending).ShouldBeNull();
    }

    [Fact]
    public async Task EnterAsync_ZeroWait_FailsImmediately()
    {
        var gate = new TableReadConcurrencyGate(new FakeTimeProvider());
        using var held = gate.TryEnter("k", 1);

        var result = gate.EnterAsync(K("k"), 1, TimeSpan.Zero, CancellationToken.None);

        result.IsCompleted.ShouldBeTrue();
        (await result).ShouldBeNull();
    }

    [Fact]
    public async Task EnterAsync_WaitersBeyondLimit_FailImmediately()
    {
        var gate = new TableReadConcurrencyGate(new FakeTimeProvider());
        var holders = new[] { gate.TryEnter("k", 2)!, gate.TryEnter("k", 2)! };
        var w1 = gate.EnterAsync(K("k"), 2, Wait, CancellationToken.None).AsTask();
        var w2 = gate.EnterAsync(K("k"), 2, Wait, CancellationToken.None).AsTask();

        var overflow = gate.EnterAsync(K("k"), 2, Wait, CancellationToken.None);

        overflow.IsCompleted.ShouldBeTrue();
        (await overflow).ShouldBeNull();
        holders[0].Dispose();
        holders[1].Dispose();
        (await w1).ShouldNotBeNull();
        (await w2).ShouldNotBeNull();
    }

    [Fact]
    public async Task EnterAsync_Waiters_AreServedFifo()
    {
        var gate = new TableReadConcurrencyGate(new FakeTimeProvider());
        var h1 = gate.TryEnter("k", 2)!;
        var h2 = gate.TryEnter("k", 2)!;
        var w1 = gate.EnterAsync(K("k"), 2, Wait, CancellationToken.None).AsTask();
        var w2 = gate.EnterAsync(K("k"), 2, Wait, CancellationToken.None).AsTask();

        h1.Dispose();
        var l1 = (await w1)!;
        w2.IsCompleted.ShouldBeFalse();
        h2.Dispose();
        var l2 = (await w2)!;
        l1.Dispose();
        l2.Dispose();
        gate.TryEnter("k", 2).ShouldNotBeNull();
    }

    [Fact]
    public async Task EnterAsync_NewCallerCannotBargePastQueuedWaiter()
    {
        var gate = new TableReadConcurrencyGate(new FakeTimeProvider());
        var held = gate.TryEnter("k", 1)!;
        var waiter = gate.EnterAsync(K("k"), 1, Wait, CancellationToken.None).AsTask();

        held.Dispose();

        // The slot belongs to the queued waiter now.
        gate.TryEnter("k", 1).ShouldBeNull();
        (await gate.EnterAsync(K("k"), 1, TimeSpan.Zero, CancellationToken.None)).ShouldBeNull();
        (await waiter).ShouldNotBeNull();
    }

    [Fact]
    public async Task EnterAsync_ConcurrentHolders_NeverExceedLimit()
    {
        var gate = new TableReadConcurrencyGate();
        const int limit = 4;
        var running = 0;
        var peak = 0;

        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            using var lease = await gate.EnterAsync(K("k"), limit, TimeSpan.FromSeconds(10), CancellationToken.None);
            // Waiters beyond the per-key cap are refused; only granted leases count.
            if (lease is null)
            {
                return;
            }

            var now = Interlocked.Increment(ref running);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }

            await Task.Yield();
            Interlocked.Decrement(ref running);
        })).ToArray();
        await Task.WhenAll(tasks);

        peak.ShouldBeLessThanOrEqualTo(limit);
        gate.TryEnter("k", limit).ShouldNotBeNull();
        running.ShouldBe(0);
    }

    [Fact]
    public async Task EnterAsync_CancelledWaiter_LeavesQueue_ThrowsAndLeaksNoSlot()
    {
        var gate = new TableReadConcurrencyGate(new FakeTimeProvider());
        var held = gate.TryEnter("k", 1)!;
        using var cts = new CancellationTokenSource();
        var cancelled = gate.EnterAsync(K("k"), 1, Wait, cts.Token).AsTask();

        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => cancelled);

        // The cancelled waiter left the queue: a new waiter can queue and receives the freed slot.
        var live = gate.EnterAsync(K("k"), 1, Wait, CancellationToken.None).AsTask();
        live.IsCompleted.ShouldBeFalse();
        held.Dispose();
        var lease = await live;
        lease.ShouldNotBeNull();
        lease.Dispose();
        gate.TryEnter("k", 1).ShouldNotBeNull();
    }

    [Fact]
    public async Task EnterAsync_TimedOutWaiter_DoesNotReceiveLaterSlot()
    {
        var time = new FakeTimeProvider();
        var gate = new TableReadConcurrencyGate(time);
        var held = gate.TryEnter("k", 1)!;
        var expired = gate.EnterAsync(K("k"), 1, Wait, CancellationToken.None).AsTask();
        time.Advance(Wait);
        (await expired).ShouldBeNull();

        held.Dispose();

        var next = gate.TryEnter("k", 1);
        next.ShouldNotBeNull();
        next.Dispose();
        gate.TryEnter("k", 1).ShouldNotBeNull();
    }

    [Fact]
    public async Task EnterAsync_OtherUserKey_IsIndependent()
    {
        var gate = new TableReadConcurrencyGate(new FakeTimeProvider());
        using var held = gate.TryEnter("t|alice|orders", 1);

        var other = await gate.EnterAsync(K("t|bob|orders"), 1, TimeSpan.Zero, CancellationToken.None);

        other.ShouldNotBeNull();
        other.Dispose();
    }

    [Fact]
    public async Task EnterAsync_MultipleKeys_SharedBudget_AllOrNothing()
    {
        var time = new FakeTimeProvider();
        var gate = new TableReadConcurrencyGate(time);
        using var blockB = gate.TryEnter("b", 1);

        // "a" is free and acquired first (ordinal order), "b" blocks and the budget expires: "a" must be released again.
        var pending = gate.EnterAsync(K("b", "a", "a"), 1, Wait, CancellationToken.None).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        time.Advance(Wait);

        (await pending).ShouldBeNull();
        var a = gate.TryEnter("a", 1);
        a.ShouldNotBeNull();
        a.Dispose();
    }

    [Fact]
    public async Task EnterAsync_MultipleKeys_AllFree_GrantsCompositeLeaseThatReleasesAll()
    {
        var gate = new TableReadConcurrencyGate(new FakeTimeProvider());

        var lease = await gate.EnterAsync(K("a", "b"), 1, Wait, CancellationToken.None);

        lease.ShouldNotBeNull();
        gate.TryEnter("a", 1).ShouldBeNull();
        gate.TryEnter("b", 1).ShouldBeNull();
        lease.Dispose();
        lease.Dispose();
        gate.TryEnter("a", 1).ShouldNotBeNull();
        gate.TryEnter("b", 1).ShouldNotBeNull();
    }

    [Fact]
    public async Task EnterAsync_UnlimitedLimit_BypassesQueue()
    {
        var gate = new TableReadConcurrencyGate(new FakeTimeProvider());

        (await gate.EnterAsync(K("k"), 0, TimeSpan.Zero, CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public async Task EnterAsync_InsideScopeWithDurationLimit_WaitEndsWithScope()
    {
        var time = new FakeTimeProvider();
        var options = ScopeQueryExecutionGuardTests.CreateGatewayOptions(scopeLimits: new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent.Read"] = new() { MaxQueryDurationSeconds = 5 }
        });
        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(options));
        var guard = new ScopeQueryExecutionGuard(resolver, time);
        var gate = new TableReadConcurrencyGate(time);
        using var held = gate.TryEnter("k", 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = guard.RunAsync(ScopeQueryExecutionGuardTests.CreatePrincipal(scp: "Agent.Read"), null, null, async (_, _) =>
        {
            var wait = gate.EnterAsync(K("k"), 1, TimeSpan.FromSeconds(10), CancellationToken.None).AsTask();
            entered.SetResult();
            try
            {
                await wait;
                outcome.SetResult(false);
            }
            catch (OperationCanceledException)
            {
                outcome.SetResult(true);
                throw;
            }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(5));

        (await outcome.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        await Should.ThrowAsync<Exception>(() => run);
    }

    [Fact]
    public async Task EnterAsync_GrantRacesCancel_NoSlotLeak()
    {
        var gate = new TableReadConcurrencyGate(new FakeTimeProvider());
        for (var i = 0; i < 1000; i++)
        {
            var held = gate.TryEnter("k", 1)!;
            using var cts = new CancellationTokenSource();
            var waiter = gate.EnterAsync(K("k"), 1, Wait, cts.Token).AsTask();

            var release = Task.Run(held.Dispose);
            var cancel = Task.Run(cts.Cancel);
            await Task.WhenAll(release, cancel);

            try
            {
                (await waiter)?.Dispose();
            }
            catch (OperationCanceledException)
            {
            }

            var probe = gate.TryEnter("k", 1);
            probe.ShouldNotBeNull($"slot leaked in iteration {i}");
            probe.Dispose();
        }
    }

    [Fact]
    public async Task EnterAsync_MultiKey_ConcurrentOppositeOrder_NoDeadlock()
    {
        var gate = new TableReadConcurrencyGate();
        var workers = Enumerable.Range(0, 2).Select(n => Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                var keys = n % 2 == 0 ? K("a", "b") : K("b", "a");
                var lease = await gate.EnterAsync(keys, 1, TimeSpan.FromSeconds(10), CancellationToken.None);
                lease.ShouldNotBeNull();
                await Task.Yield();
                lease.Dispose();
            }
        })).ToArray();

        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));
        gate.TryEnter("a", 1).ShouldNotBeNull().Dispose();
        gate.TryEnter("b", 1).ShouldNotBeNull().Dispose();
    }

    [Fact]
    public async Task EnterAsync_AfterAllDone_KeyStateRemoved()
    {
        var gate = new TableReadConcurrencyGate(new FakeTimeProvider());
        var held = gate.TryEnter("k", 1)!;
        var waiter = gate.EnterAsync(K("k"), 1, Wait, CancellationToken.None).AsTask();
        held.Dispose();
        (await waiter)!.Dispose();

        var field = typeof(TableReadConcurrencyGate).GetField("_states", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((System.Collections.ICollection)field.GetValue(gate)!).Count.ShouldBe(0);
    }
}
