namespace Autheris.Tests.Unit.Caching;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Caching;
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
}
