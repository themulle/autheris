namespace Autheris.Tests.Unit.Caching;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.State.Caching;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

/// <summary>WP-PF-06 / SEC-SC-09: eviction only produces misses; the cache never grows beyond its bound.</summary>
public sealed class BoundedCacheTests
{
    [Fact]
    public void Count_NeverExceedsCapacity_AndEvictionOnlyProducesMisses()
    {
        var cache = new BoundedCache<int, string>(capacity: 100);
        for (var i = 0; i < 5_000; i++)
        {
            cache.Set(i, "v" + i);
        }

        cache.Count.ShouldBeLessThanOrEqualTo(100);
        var hits = Enumerable.Range(0, 5_000).Count(i => cache.TryGet(i, out var v) && v == "v" + i);
        hits.ShouldBeLessThanOrEqualTo(100);
        hits.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Entry_ExpiresAfterTtl_UsingInjectedClock()
    {
        var time = new FakeTimeProvider();
        var cache = new BoundedCache<string, int>(10, TimeSpan.FromSeconds(30), time);
        cache.Set("a", 1);

        time.Advance(TimeSpan.FromSeconds(29));
        cache.TryGet("a", out var v).ShouldBeTrue();
        v.ShouldBe(1);

        time.Advance(TimeSpan.FromSeconds(2));
        cache.TryGet("a", out _).ShouldBeFalse();
    }

    [Fact]
    public void GetOrAdd_ComputesOncePerKey()
    {
        var cache = new BoundedCache<string, int>(10);
        var calls = 0;
        cache.GetOrAdd("k", _ => ++calls).ShouldBe(1);
        cache.GetOrAdd("k", _ => ++calls).ShouldBe(1);
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task GetOrAddAsync_ConcurrentMisses_ShareOneFactoryCall()
    {
        var cache = new BoundedCache<string, int>(10);
        var calls = 0;
        var gate = new TaskCompletionSource();

        async Task<int> Factory(string _)
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return 42;
        }

        var tasks = Enumerable.Range(0, 50).Select(_ => cache.GetOrAddAsync("k", Factory)).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        results.ShouldAllBe(r => r == 42);
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task GetOrAddAsync_FactoryFailure_IsNotCached()
    {
        var cache = new BoundedCache<string, int>(10);
        var calls = 0;
        await Should.ThrowAsync<InvalidOperationException>(() => cache.GetOrAddAsync("k", _ => { calls++; throw new InvalidOperationException(); }));

        (await cache.GetOrAddAsync("k", _ => Task.FromResult(++calls))).ShouldBe(2);
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var cache = new BoundedCache<int, int>(10);
        cache.Set(1, 1);
        cache.Clear();
        cache.Count.ShouldBe(0);
        cache.TryGet(1, out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TryRemove_returns_removed_value()
    {
        var cache = new BoundedCache<string, string>(10);
        cache.Set("k1", "v1");

        cache.TryRemove("k1", out var removed).ShouldBeTrue();
        removed.ShouldBe("v1");
        cache.TryGet("k1", out _).ShouldBeFalse();
        cache.TryRemove("missing", out var missing).ShouldBeFalse();
        missing.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task First_caller_cancellation_does_not_fail_other_waiters()
    {
        var cache = new BoundedCache<string, string>(10);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;
        var seenTokenCancelled = false;

        Task<string> Factory(string _, CancellationToken token) => Task.Run(async () =>
        {
            Interlocked.Increment(ref factoryCalls);
            started.TrySetResult();
            await gate.Task.ConfigureAwait(false);
            seenTokenCancelled = token.IsCancellationRequested;
            return "shared-value";
        });

        using var cts1 = new CancellationTokenSource();
        var waiter1 = cache.GetOrAddAsync("k", Factory, cts1.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiter2 = cache.GetOrAddAsync("k", Factory, CancellationToken.None);

        cts1.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => waiter1.WaitAsync(TimeSpan.FromSeconds(5)));

        gate.SetResult();
        (await waiter2.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("shared-value");
        seenTokenCancelled.ShouldBeFalse();
        Volatile.Read(ref factoryCalls).ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Failed_factory_is_not_cached()
    {
        var cache = new BoundedCache<string, int>(10);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        Task<int> FailingFactory(string _, CancellationToken __) => Task.Run<int>(async () =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await gate.Task.ConfigureAwait(false);
            throw new InvalidOperationException("boom");
        });

        var w1 = cache.GetOrAddAsync("k", FailingFactory, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var w2 = cache.GetOrAddAsync("k", FailingFactory, CancellationToken.None);
        gate.SetResult();

        await Should.ThrowAsync<InvalidOperationException>(() => w1.WaitAsync(TimeSpan.FromSeconds(5)));
        await Should.ThrowAsync<InvalidOperationException>(() => w2.WaitAsync(TimeSpan.FromSeconds(5)));
        Volatile.Read(ref calls).ShouldBe(1);
        cache.TryGet("k", out _).ShouldBeFalse();

        var recovered = await cache.GetOrAddAsync("k", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(42);
        }, CancellationToken.None);
        recovered.ShouldBe(42);
        Volatile.Read(ref calls).ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Factory_timeout_does_not_block_waiters()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-14T12:00:00Z"));
        var cache = new BoundedCache<string, string>(10, time: time, factoryTimeout: TimeSpan.FromSeconds(2));
        var never = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        var w1 = cache.GetOrAddAsync("k", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return never.Task;
        }, CancellationToken.None);
        var w2 = cache.GetOrAddAsync("k", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return never.Task;
        }, CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(3));

        await Should.ThrowAsync<TimeoutException>(() => w1.WaitAsync(TimeSpan.FromSeconds(5)));
        await Should.ThrowAsync<TimeoutException>(() => w2.WaitAsync(TimeSpan.FromSeconds(5)));
        Volatile.Read(ref calls).ShouldBe(1);
        cache.TryGet("k", out _).ShouldBeFalse();

        var next = await cache.GetOrAddAsync("k", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("ok");
        }, CancellationToken.None);
        next.ShouldBe("ok");
        Volatile.Read(ref calls).ShouldBe(2);
    }
}
