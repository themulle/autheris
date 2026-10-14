using TrinoSqlEngine.Parsing;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>WP-ARC-C-11 (ARC-S-03): parsing runs on long-lived pooled threads, never on a thread per parse.</summary>
[Trait("Category", "SqlGate")]
public class ParseWorkerPoolTests
{
    // A stack size no other test uses: its process-wide pool is only touched by this class.
    private const int IsolatedStackSize = 3 * 1024 * 1024 + 4096;

    private static void WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void ThousandParses_ReuseTheWorkerThreads()
    {
        var engine = new FastSqlEngine { ParseThreadStackSize = IsolatedStackSize };
        var pool = ParseWorkerPool.ForStackSize(IsolatedStackSize);
        int before = pool.ThreadsCreated;

        for (int i = 0; i < 1000; i++)
        {
            Assert.NotNull(engine.Parse("SELECT id FROM orders".AsMemory()).Tree);
        }

        int created = pool.ThreadsCreated - before;
        Assert.True(created <= pool.Size, $"{created} threads were created for 1000 sequential parses (pool size {pool.Size}).");
    }

    [Fact]
    public void Work_RunsWithTheAmbientContextOfTheSubmitter()
    {
        using var pool = new ParseWorkerPool(1, FastSqlEngine.MinParseThreadStackSize);
        var ambient = new AsyncLocal<int>();
        ambient.Value = 42;
        int observed = 0;
        using var done = new ManualResetEventSlim(false);

        Assert.True(pool.TrySubmit(new ParseWork(() => { observed = ambient.Value; done.Set(); }), 1000, CancellationToken.None));

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(42, observed);
    }

    [Fact]
    public void FullQueue_RejectsWithinTheBudget()
    {
        using var pool = new ParseWorkerPool(1, FastSqlEngine.MinParseThreadStackSize);
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        Assert.True(pool.TrySubmit(new ParseWork(() => { started.Set(); release.Wait(); }), 1000, CancellationToken.None));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        // The single worker is busy: the bounded queue (16 slots for a pool of one) fills up.
        for (int i = 0; i < 16; i++)
        {
            Assert.True(pool.TrySubmit(new ParseWork(static () => { }), 1000, CancellationToken.None));
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(pool.TrySubmit(new ParseWork(static () => { }), 100, CancellationToken.None));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));

        release.Set();
    }

    [Fact]
    public void AbandonedWorker_IsReplaced_AndRetiresWhenItsWorkReturns()
    {
        using var pool = new ParseWorkerPool(1, FastSqlEngine.MinParseThreadStackSize);
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var stuck = new ParseWork(() => { started.Set(); release.Wait(); });

        Assert.True(pool.TrySubmit(stuck, 1000, CancellationToken.None));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        pool.Abandon(stuck);

        // The next parse is served by a replacement thread although the abandoned one is still busy.
        using var second = new ManualResetEventSlim(false);
        Assert.True(pool.TrySubmit(new ParseWork(() => second.Set()), 1000, CancellationToken.None));
        Assert.True(second.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, pool.ThreadsCreated);

        release.Set();

        // The retired thread is not reused: later work still runs and no third thread is needed.
        using var third = new ManualResetEventSlim(false);
        Assert.True(pool.TrySubmit(new ParseWork(() => third.Set()), 1000, CancellationToken.None));
        Assert.True(third.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, pool.ThreadsCreated);
    }

    [Fact]
    public void TimedOutParse_ReleasesItsSlot_AndTheEngineStaysUsable()
    {
        using var limiter = new SemaphoreSlim(1, 1);
        var engine = new FastSqlEngine { ParseConcurrencyLimiter = limiter, ParseTimeout = TimeSpan.FromTicks(1), ParseThreadStackSize = IsolatedStackSize };
        string sql = "SELECT * FROM orders WHERE id IN (" + string.Join(", ", Enumerable.Range(0, 5000)) + ")";

        Assert.ThrowsAny<Antlr4.Runtime.Misc.ParseCanceledException>(() => engine.Parse(sql.AsMemory()));

        WaitFor(() => limiter.CurrentCount == 1, "the parser slot to be released");
        engine.ParseTimeout = TimeSpan.FromSeconds(30);
        Assert.NotNull(engine.Parse(sql.AsMemory()).Tree);
    }

    [Fact]
    public void CanceledWhileQueued_ReleasesTheSlot()
    {
        using var limiter = new SemaphoreSlim(1, 1);
        var engine = new FastSqlEngine { ParseConcurrencyLimiter = limiter, ParseThreadStackSize = IsolatedStackSize };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => engine.Parse("SELECT 1".AsMemory(), null, cts.Token));

        Assert.Equal(1, limiter.CurrentCount);
    }
}
