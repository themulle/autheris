namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// F-GOV-14 CR-07 (SEC-10): single-flight loading is per cache key. A slow key source of one filter or tenant never
/// delays the cold load of another cache key (no shared lock stripes); failures are never cached.
/// </summary>
public sealed class VirtualFilterKeySetCacheTests : IDisposable
{
    private static readonly VirtualFilterKeyType[] Types = [VirtualFilterKeyType.IntegerNumber];
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly VirtualFilterKeySetCache _cache = new(Options.Create(new GatewayOptions()));

    public void Dispose() => _cache.Dispose();

    private static VirtualFilterKeySet Keys(long value) => VirtualFilterKeySet.Create(Types, [[value]]);

    private ValueTask<VirtualFilterKeySet> Get(string key, Func<CancellationToken, Task<VirtualFilterKeySet>> load, CancellationToken ct = default) =>
        _cache.GetOrLoadAsync(key, Ttl, load, ct);

    [Fact]
    public async Task SlowLoadOfOneKey_DoesNotDelayAnyOtherKey()
    {
        // 200 other keys: with 32 shared lock stripes several of them would wait behind the slow one.
        var release = new TaskCompletionSource();
        var slow = Get("vfkeys:tenant-a:slow", async _ =>
        {
            await release.Task;
            return Keys(1);
        }).AsTask();

        var others = Enumerable.Range(0, 200).Select(i => Get($"vfkeys:tenant-b:filter-{i}", _ => Task.FromResult(Keys(i))).AsTask()).ToArray();

        var results = await Task.WhenAll(others).WaitAsync(Timeout);
        slow.IsCompleted.ShouldBeFalse();
        results.Length.ShouldBe(200);

        release.SetResult();
        (await slow.WaitAsync(Timeout)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentColdCallsOfTheSameKey_LoadOnce_AllGetTheResult()
    {
        int loads = 0;
        var release = new TaskCompletionSource();

        var calls = Enumerable.Range(0, 20).Select(_ => Get("vfkeys:k", async _ =>
        {
            Interlocked.Increment(ref loads);
            await release.Task;
            return Keys(7);
        }).AsTask()).ToArray();
        release.SetResult();

        var results = await Task.WhenAll(calls).WaitAsync(Timeout);

        loads.ShouldBe(1);
        results.ShouldAllBe(r => r.Count == 1);
        results.Count(r => !r.CacheHit).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task FailedLoad_IsSeenByTheWaiters_NotCached_NextCallLoadsAgain()
    {
        int loads = 0;
        var release = new TaskCompletionSource();
        Task<VirtualFilterKeySet> Failing(CancellationToken _) => Task.Run<VirtualFilterKeySet>(async () =>
        {
            Interlocked.Increment(ref loads);
            await release.Task;
            throw new InvalidOperationException("source down");
        });

        var calls = Enumerable.Range(0, 5).Select(_ => Get("vfkeys:f", Failing).AsTask()).ToArray();
        release.SetResult();
        foreach (var call in calls)
        {
            await Should.ThrowAsync<InvalidOperationException>(() => call.WaitAsync(Timeout));
        }

        loads.ShouldBe(1);
        (await Get("vfkeys:f", _ => Task.FromResult(Keys(3)))).CacheHit.ShouldBeFalse();
        (await Get("vfkeys:f", _ => throw new InvalidOperationException("must be cached"))).CacheHit.ShouldBeTrue();
    }

    [Fact]
    public async Task OneWaiterCancelling_DoesNotCancelTheLoadForTheOthers()
    {
        var release = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        Func<CancellationToken, Task<VirtualFilterKeySet>> load = async _ =>
        {
            await release.Task;
            return Keys(9);
        };
        var first = Get("vfkeys:c", load, cts.Token).AsTask();
        var second = Get("vfkeys:c", load).AsTask();

        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => first.WaitAsync(Timeout));
        release.SetResult();

        (await second.WaitAsync(Timeout)).Count.ShouldBe(1);
    }
}
