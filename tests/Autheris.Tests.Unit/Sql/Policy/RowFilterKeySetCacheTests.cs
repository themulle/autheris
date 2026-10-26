using Autheris.Tests.Common;
namespace Autheris.Tests.Unit.Sql.Policy;

using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Tests.Unit.VirtualFilters.Federation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #107 cache level: SEC-RF107-02 (tenant / data source re-check), SEC-RF107-04 (absolute expiry, strict age check, joiners),
/// SEC-RF107-13 (oversize sentinel, concurrency limit, in-flight join), SEC-RF107-08 (failures never cached).
/// </summary>
[Trait("Category", "Security")]
public sealed class RowFilterKeySetCacheTests : IDisposable
{
    private const string Key = "k1";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly List<(LogLevel Level, string Message)> _logs = [];
    private readonly List<RowFilterKeySetCache> _caches = [];

    public void Dispose()
    {
        foreach (var cache in _caches)
        {
            cache.Dispose();
        }
    }

    private RowFilterKeySetCache Cache(long sizeLimit = 1024 * 1024, int concurrency = 4)
    {
        var cache = new RowFilterKeySetCache(sizeLimit, concurrency, _time, new ListLogger<RowFilterKeySetCache>(_logs));
        _caches.Add(cache);
        return cache;
    }

    private RowFilterKeySetEntry Entry(string tenant = "t1", string dataSource = "ds", int keys = 3, DateTimeOffset? loadedAt = null)
        => new(Enumerable.Range(1, keys).Select(i => (object)(long)i).ToArray(), false, loadedAt ?? _time.GetUtcNow(), tenant, dataSource);

    private ValueTask<RowFilterKeySetCacheResult> Get(RowFilterKeySetCache cache, Func<CancellationToken, Task<RowFilterKeySetEntry>> load,
        string key = Key, string tenant = "t1", string dataSource = "ds", TimeSpan? duration = null, CancellationToken ct = default)
        => cache.GetOrLoadAsync(key, tenant, dataSource, duration ?? TimeSpan.FromSeconds(60), load, Timeout, ct);

    [Fact]
    public async Task MissLoads_ThenHitsWithoutLoading()
    {
        var cache = Cache();
        int loads = 0;
        Task<RowFilterKeySetEntry> Load(CancellationToken _) { loads++; return Task.FromResult(Entry()); }

        var first = await Get(cache, Load);
        _time.Advance(TimeSpan.FromSeconds(10));
        var second = await Get(cache, Load);

        first.Status.ShouldBe(RowFilterKeySetCacheStatus.Miss);
        second.Status.ShouldBe(RowFilterKeySetCacheStatus.Hit);
        second.AgeSeconds.ShouldBe(10);
        second.Entry!.Keys.Count.ShouldBe(3);
        loads.ShouldBe(1);
    }

    [Fact]
    public async Task AgeCheck_IsStrict_SEC_RF107_04_c()
    {
        var cache = Cache();
        int loads = 0;
        Task<RowFilterKeySetEntry> Load(CancellationToken _) { loads++; return Task.FromResult(Entry()); }
        await Get(cache, Load);

        _time.Advance(TimeSpan.FromSeconds(59));
        (await Get(cache, Load)).Status.ShouldBe(RowFilterKeySetCacheStatus.Hit);

        _time.Advance(TimeSpan.FromSeconds(1));   // age == duration is already too old
        (await Get(cache, Load)).Status.ShouldBe(RowFilterKeySetCacheStatus.Miss);
        loads.ShouldBe(2);
    }

    [Fact]
    public async Task LoweredDuration_ForcesReload_AC12()
    {
        var cache = Cache();
        int loads = 0;
        Task<RowFilterKeySetEntry> Load(CancellationToken _) { loads++; return Task.FromResult(Entry()); }
        await Get(cache, Load, duration: TimeSpan.FromSeconds(300));

        _time.Advance(TimeSpan.FromSeconds(61));

        (await Get(cache, Load, duration: TimeSpan.FromSeconds(300))).Status.ShouldBe(RowFilterKeySetCacheStatus.Hit);
        (await Get(cache, Load, duration: TimeSpan.FromSeconds(60))).Status.ShouldBe(RowFilterKeySetCacheStatus.Miss);
        loads.ShouldBe(2);
    }

    [Fact]
    public async Task Expiry_IsAbsoluteFromLoadedAt_AndNeverSliding_SEC_RF107_04_b()
    {
        var cache = Cache();
        int loads = 0;
        Task<RowFilterKeySetEntry> Load(CancellationToken _) { loads++; return Task.FromResult(Entry()); }
        await Get(cache, Load);

        for (int i = 0; i < 6; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(11));   // reads every 11 s must not keep the entry alive past 60 s
            await Get(cache, Load);
        }

        loads.ShouldBe(2);
    }

    [Fact]
    public async Task EntryWithEarlierLoadedAt_ExpiresAtLoadedAtPlusDuration_NotAtLoadCompletion_SEC_RF107_04_b()
    {
        var cache = Cache();
        int loads = 0;
        // The key query started 50 s ago (slow source); the entry may live only 10 s more.
        Task<RowFilterKeySetEntry> Load(CancellationToken _) { loads++; return Task.FromResult(Entry(loadedAt: _time.GetUtcNow() - TimeSpan.FromSeconds(50))); }
        await Get(cache, Load);

        _time.Advance(TimeSpan.FromSeconds(9));
        (await Get(cache, Load)).Status.ShouldBe(RowFilterKeySetCacheStatus.Hit);
        _time.Advance(TimeSpan.FromSeconds(2));
        (await Get(cache, Load)).Status.ShouldBe(RowFilterKeySetCacheStatus.Miss);
        loads.ShouldBe(2);
    }

    [Fact]
    public async Task ForgedEntryOfOtherTenant_IsTreatedAsMiss_RemovedAndWarned_SEC_RF107_02()
    {
        var cache = Cache();
        cache.InsertForTest(Key, Entry(tenant: "tenant-b"), TimeSpan.FromSeconds(60));
        int loads = 0;
        Task<RowFilterKeySetEntry> Load(CancellationToken _) { loads++; return Task.FromResult(Entry(tenant: "tenant-a")); }

        var result = await Get(cache, Load, tenant: "tenant-a");

        result.Status.ShouldBe(RowFilterKeySetCacheStatus.Miss);
        loads.ShouldBe(1);
        result.Entry!.Tenant.ShouldBe("tenant-a");
        _logs.ShouldContain(l => l.Level == LogLevel.Warning);
        _logs.ShouldAllBe(l => !l.Message.Contains("tenant-a") && !l.Message.Contains("tenant-b"), "no tenant value in the log");
        (await Get(cache, Load, tenant: "tenant-a")).Status.ShouldBe(RowFilterKeySetCacheStatus.Hit);
    }

    [Fact]
    public async Task ForgedEntryOfOtherDataSource_IsTreatedAsMiss_SEC_RF107_02()
    {
        var cache = Cache();
        cache.InsertForTest(Key, Entry(dataSource: "other-ds"), TimeSpan.FromSeconds(60));
        int loads = 0;

        var result = await Get(cache, _ => { loads++; return Task.FromResult(Entry(dataSource: "ds")); }, dataSource: "ds");

        result.Status.ShouldBe(RowFilterKeySetCacheStatus.Miss);
        loads.ShouldBe(1);
    }

    [Fact]
    public async Task TenantComparison_IsOrdinal_SEC_RF107_02()
    {
        var cache = Cache();
        cache.InsertForTest(Key, Entry(tenant: "Tenant-A"), TimeSpan.FromSeconds(60));
        int loads = 0;

        await Get(cache, _ => { loads++; return Task.FromResult(Entry(tenant: "tenant-a")); }, tenant: "tenant-a");

        loads.ShouldBe(1);
    }

    [Fact]
    public async Task LoadReturningAnEntryOfAnotherTenant_Fails_NothingCached_SEC_RF107_02()
    {
        var cache = Cache();

        await Should.ThrowAsync<InvalidOperationException>(async () => await Get(cache, _ => Task.FromResult(Entry(tenant: "someone-else")), tenant: "t1"));

        cache.TryGetRaw(Key).ShouldBeNull();
    }

    [Fact]
    public async Task OversizedEntry_BecomesAnOverflowSentinel_AndIsCached_SEC_RF107_13_b()
    {
        var cache = Cache(sizeLimit: 8 * 1024);   // 1 KiB per entry at most
        int loads = 0;
        Task<RowFilterKeySetEntry> Load(CancellationToken _) { loads++; return Task.FromResult(Entry(keys: 200)); }

        var first = await Get(cache, Load);
        var second = await Get(cache, Load);

        first.Entry!.Overflow.ShouldBeTrue();
        first.Entry.Keys.ShouldBeEmpty();
        second.Status.ShouldBe(RowFilterKeySetCacheStatus.Hit);
        second.Entry!.Overflow.ShouldBeTrue();
        loads.ShouldBe(1);
        cache.CurrentEstimatedSize.ShouldBeLessThan(2 * 1024);
    }

    [Fact]
    public async Task EntryJustUnderTheThreshold_IsStoredAsKeys()
    {
        var cache = Cache(sizeLimit: 64 * 1024);

        var result = await Get(cache, _ => Task.FromResult(Entry(keys: 10)));

        result.Entry!.Overflow.ShouldBeFalse();
        result.Entry.Keys.Count.ShouldBe(10);
    }

    [Fact]
    public async Task ConcurrencyLimit_FallsBackWithoutLoad_JoinersAreNotThrottled_SEC_RF107_13_d()
    {
        var cache = Cache(concurrency: 1);
        var gate = new TaskCompletionSource();
        int loads = 0;
        var first = Get(cache, async _ => { Interlocked.Increment(ref loads); await gate.Task; return Entry(); }, key: "a").AsTask();
        await WaitUntil(() => Volatile.Read(ref loads) == 1);

        var other = await Get(cache, _ => { Interlocked.Increment(ref loads); return Task.FromResult(Entry()); }, key: "b");
        var joiner = Get(cache, _ => { Interlocked.Increment(ref loads); return Task.FromResult(Entry()); }, key: "a").AsTask();

        other.Status.ShouldBe(RowFilterKeySetCacheStatus.Throttled);
        other.Entry.ShouldBeNull();
        joiner.IsCompleted.ShouldBeFalse();
        gate.SetResult();
        (await first.WaitAsync(Wait)).Status.ShouldBe(RowFilterKeySetCacheStatus.Miss);
        (await joiner.WaitAsync(Wait)).Entry.ShouldNotBeNull();
        loads.ShouldBe(1);
        cache.TryGetRaw("b").ShouldBeNull();   // a throttled load caches nothing

        (await Get(cache, _ => Task.FromResult(Entry()), key: "b")).Status.ShouldBe(RowFilterKeySetCacheStatus.Miss);   // the slot was released
    }

    [Fact]
    public async Task FailedLoad_ReleasesTheSlot_NothingCached_SEC_RF107_08()
    {
        var cache = Cache(concurrency: 1);

        await Should.ThrowAsync<InvalidOperationException>(async () => await Get(cache, _ => throw new InvalidOperationException("down")));

        cache.TryGetRaw(Key).ShouldBeNull();
        (await Get(cache, _ => Task.FromResult(Entry()))).Status.ShouldBe(RowFilterKeySetCacheStatus.Miss);
    }

    [Fact]
    public async Task ParallelColdCallers_ShareOneLoad_AC5()
    {
        var cache = Cache();
        var gate = new TaskCompletionSource();
        int loads = 0;
        var calls = Enumerable.Range(0, 32).Select(_ => Get(cache, async _ => { Interlocked.Increment(ref loads); await gate.Task; return Entry(); }).AsTask()).ToArray();

        gate.SetResult();
        var results = await Task.WhenAll(calls).WaitAsync(Wait);

        loads.ShouldBe(1);
        results.ShouldAllBe(r => r.Entry != null && r.Status == RowFilterKeySetCacheStatus.Miss);
    }

    [Fact]
    public async Task NoStaleServeWhileReloadInFlight_SEC_RF107_04_d()
    {
        var cache = Cache();
        await Get(cache, _ => Task.FromResult(Entry(keys: 1)));   // the "old" entry
        _time.Advance(TimeSpan.FromSeconds(61));                 // too old now
        var gate = new TaskCompletionSource();
        var reload = Get(cache, async _ => { await gate.Task; return Entry(keys: 2); }).AsTask();
        var joiner = Get(cache, _ => Task.FromResult(Entry(keys: 3))).AsTask();

        joiner.IsCompleted.ShouldBeFalse();   // it neither gets the old entry nor starts a second load
        gate.SetResult();

        (await reload.WaitAsync(Wait)).Entry!.Keys.Count.ShouldBe(2);
        (await joiner.WaitAsync(Wait)).Entry!.Keys.Count.ShouldBe(2);
    }

    [Fact]
    public async Task CallerCancellation_DoesNotCancelTheSharedLoad()
    {
        var cache = Cache();
        var gate = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var first = Get(cache, async _ => { await gate.Task; return Entry(); }, ct: cts.Token).AsTask();
        var second = Get(cache, _ => Task.FromResult(Entry())).AsTask();

        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => first.WaitAsync(Wait));
        gate.SetResult();

        (await second.WaitAsync(Wait)).Entry.ShouldNotBeNull();
    }

    [Fact]
    public async Task LoadTimeout_FailsTheWaiters_NothingCached()
    {
        var cache = Cache();
        var never = new TaskCompletionSource<RowFilterKeySetEntry>();
        var call = Get(cache, _ => never.Task).AsTask();

        _time.Advance(TimeSpan.FromSeconds(6));

        var ex = await Should.ThrowAsync<Autheris.Domain.Exceptions.GatewaySecurityException>(() => call.WaitAsync(Wait));
        ex.ErrorCode.ShouldBe(RowFilterKeySetErrorCodes.KeySetUnavailable);
        ex.InnerException.ShouldBeOfType<TimeoutException>();
        cache.TryGetRaw(Key).ShouldBeNull();
    }

    private static Task WaitUntil(Func<bool> condition) =>
        WaitHelper.WaitUntil(condition, Wait, pollInterval: TimeSpan.FromMilliseconds(5));
}
