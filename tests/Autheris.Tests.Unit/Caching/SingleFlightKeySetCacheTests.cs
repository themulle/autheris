namespace Autheris.Tests.Unit.Caching;

using Autheris.Application.Caching;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #107 (PLAN-GOV-107 A-5, SEC-RF107-04 d): generic single-flight cache. Single flight per key, failures never cached, caller
/// cancellation never cancels the shared load, the load is bounded by its own timeout, expiration is absolute.
/// </summary>
public sealed class SingleFlightKeySetCacheTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly SingleFlightKeySetCache<string> _cache;

    public SingleFlightKeySetCacheTests() => _cache = new SingleFlightKeySetCache<string>(1024 * 1024, _time);

    public void Dispose() => _cache.Dispose();

    private SingleFlightLoadResult<string> Result(string value, TimeSpan? lifetime = null, long size = 10)
        => new(value, _time.GetUtcNow() + (lifetime ?? TimeSpan.FromMinutes(5)), size);

    [Fact]
    public async Task ParallelColdCallers_ShareOneLoad()
    {
        int loads = 0;
        var gate = new TaskCompletionSource();
        var calls = Enumerable.Range(0, 32).Select(_ => _cache.GetOrLoadAsync("k", async _ =>
        {
            Interlocked.Increment(ref loads);
            await gate.Task;
            return Result("v");
        }, CancellationToken.None).AsTask()).ToArray();

        gate.SetResult();
        var values = await Task.WhenAll(calls).WaitAsync(Wait);

        loads.ShouldBe(1);
        values.ShouldAllBe(v => v == "v");
        _cache.TryGet("k", out var cached).ShouldBeTrue();
        cached.ShouldBe("v");
    }

    [Fact]
    public async Task FailedLoad_IsSeenByWaiters_NotCached_NextCallLoadsAgain()
    {
        int loads = 0;
        var gate = new TaskCompletionSource();
        var calls = Enumerable.Range(0, 4).Select(_ => _cache.GetOrLoadAsync("k", async _ =>
        {
            Interlocked.Increment(ref loads);
            await gate.Task;
            throw new InvalidOperationException("down");
        }, CancellationToken.None).AsTask()).ToArray();
        gate.SetResult();

        foreach (var call in calls)
        {
            await Should.ThrowAsync<InvalidOperationException>(() => call.WaitAsync(Wait));
        }

        loads.ShouldBe(1);
        _cache.TryGet("k", out _).ShouldBeFalse();
        (await _cache.GetOrLoadAsync("k", _ => Task.FromResult(Result("again")), CancellationToken.None)).ShouldBe("again");
    }

    [Fact]
    public async Task CallerCancellation_DoesNotCancelTheSharedLoad()
    {
        var gate = new TaskCompletionSource();
        bool tokenCancelled = false;
        using var cts = new CancellationTokenSource();
        Func<CancellationToken, Task<SingleFlightLoadResult<string>>> load = async token =>
        {
            await gate.Task;
            tokenCancelled = token.IsCancellationRequested;
            return Result("v");
        };
        var first = _cache.GetOrLoadAsync("k", load, cts.Token).AsTask();
        var second = _cache.GetOrLoadAsync("k", load, CancellationToken.None).AsTask();

        cts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => first.WaitAsync(Wait));
        gate.SetResult();

        (await second.WaitAsync(Wait)).ShouldBe("v");
        tokenCancelled.ShouldBeFalse();
    }

    [Fact]
    public async Task Load_IsBoundedByItsTimeout_AndFailureIsNotCached()
    {
        var neverCompletes = new TaskCompletionSource<SingleFlightLoadResult<string>>();
        var call = _cache.GetOrLoadAsync("k", _ => neverCompletes.Task, CancellationToken.None, TimeSpan.FromSeconds(5)).AsTask();

        _time.Advance(TimeSpan.FromSeconds(6));

        await Should.ThrowAsync<TimeoutException>(() => call.WaitAsync(Wait));
        _cache.TryGet("k", out _).ShouldBeFalse();
        (await _cache.GetOrLoadAsync("k", _ => Task.FromResult(Result("ok")), CancellationToken.None)).ShouldBe("ok");
    }

    [Fact]
    public async Task TimeoutCancelsTheTokenOfTheLoad()
    {
        var seen = new TaskCompletionSource<bool>();
        var call = _cache.GetOrLoadAsync("k", async token =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                seen.SetResult(true);
                throw;
            }

            return Result("never");
        }, CancellationToken.None, TimeSpan.FromSeconds(5)).AsTask();

        _time.Advance(TimeSpan.FromSeconds(6));

        (await seen.Task.WaitAsync(Wait)).ShouldBeTrue();
        await Should.ThrowAsync<Exception>(() => call.WaitAsync(Wait));
    }

    [Fact]
    public async Task Expiration_IsAbsolute_AndNeverSliding()
    {
        await _cache.GetOrLoadAsync("k", _ => Task.FromResult(Result("v", TimeSpan.FromSeconds(60))), CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(40));
        _cache.TryGet("k", out _).ShouldBeTrue();   // a read must not extend the lifetime
        _time.Advance(TimeSpan.FromSeconds(30));

        _cache.TryGet("k", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Remove_DropsTheEntry()
    {
        await _cache.GetOrLoadAsync("k", _ => Task.FromResult(Result("v")), CancellationToken.None);

        _cache.Remove("k");

        _cache.TryGet("k", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task EntryLargerThanTheLimit_IsNotStored_NoThrow()
    {
        using var small = new SingleFlightKeySetCache<string>(100, _time);

        (await small.GetOrLoadAsync("k", _ => Task.FromResult(Result("v", size: 1000)), CancellationToken.None)).ShouldBe("v");

        small.TryGet("k", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task CurrentEstimatedSize_CountsStoredEntries()
    {
        await _cache.GetOrLoadAsync("a", _ => Task.FromResult(Result("v", size: 30)), CancellationToken.None);
        await _cache.GetOrLoadAsync("b", _ => Task.FromResult(Result("v", size: 12)), CancellationToken.None);

        _cache.CurrentEstimatedSize.ShouldBe(42);
    }

    [Fact]
    public async Task AcceptPredicate_RejectsAStaleRacedEntry_ForTheJoiner_SEC_RF107_04()
    {
        // An entry the caller does not accept (for example older than the current duration) is never returned; a new load runs.
        await _cache.GetOrLoadAsync("k", _ => Task.FromResult(Result("old")), CancellationToken.None);

        var value = await _cache.GetOrLoadAsync("k", _ => Task.FromResult(Result("new")), CancellationToken.None, accept: v => v != "old");

        value.ShouldBe("new");
    }

    [Fact]
    public async Task JoinerOfInFlightLoad_GetsTheNewResult_NotTheOldEntry_SEC_RF107_04_d()
    {
        await _cache.GetOrLoadAsync("k", _ => Task.FromResult(Result("old")), CancellationToken.None);
        _cache.Remove("k");   // the caller found the old entry unusable and dropped it
        var gate = new TaskCompletionSource();
        var first = _cache.GetOrLoadAsync("k", async _ => { await gate.Task; return Result("new"); }, CancellationToken.None).AsTask();
        var joiner = _cache.GetOrLoadAsync("k", _ => Task.FromResult(Result("second-load")), CancellationToken.None).AsTask();

        joiner.IsCompleted.ShouldBeFalse();
        gate.SetResult();

        (await first.WaitAsync(Wait)).ShouldBe("new");
        (await joiner.WaitAsync(Wait)).ShouldBe("new");
    }
}
