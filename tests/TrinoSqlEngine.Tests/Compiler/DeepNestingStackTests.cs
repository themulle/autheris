using Antlr4.Runtime.Misc;
using TrinoSqlEngine.Parsing;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>WP-ARC-C-11: the parse limits hold on the pooled threads and on the optional inline path.</summary>
[Trait("Category", "SqlGate")]
public class DeepNestingStackTests
{
    private const int IsolatedStackSize = 3 * 1024 * 1024 + 8192;

    private static string Nested(int depth) => "SELECT " + new string('(', depth) + "1" + new string(')', depth);

    [Theory]
    [InlineData(0)]
    [InlineData(4096)]
    public void DeepNesting_IsRejectedBeforeAnyRecursion(int inlineMaxLength)
    {
        var engine = new FastSqlEngine { ParseThreadStackSize = IsolatedStackSize, InlineParseMaxLength = inlineMaxLength };

        Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse(Nested(2000).AsMemory()));
        Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse(Nested(150).AsMemory()));
        Assert.NotNull(engine.Parse(Nested(20).AsMemory()).Tree);
    }

    [Fact]
    public void InlinePath_IsOffByDefault_AndRunsWithoutPoolThreadsWhenEnabled()
    {
        Assert.Equal(0, new FastSqlEngine().InlineParseMaxLength);

        var pool = ParseWorkerPool.ForStackSize(IsolatedStackSize);
        int before = pool.ThreadsCreated;
        var engine = new FastSqlEngine { ParseThreadStackSize = IsolatedStackSize, InlineParseMaxLength = 4096 };

        Assert.NotNull(engine.Parse("SELECT id FROM orders".AsMemory()).Tree);

        Assert.Equal(before, pool.ThreadsCreated);
    }

    [Fact]
    public void InlinePath_KeepsTheConcurrencyLimiter_AndReleasesTheSlot()
    {
        using var limiter = new SemaphoreSlim(1, 1);
        var engine = new FastSqlEngine { ParseThreadStackSize = IsolatedStackSize, InlineParseMaxLength = 4096, ParseConcurrencyLimiter = limiter };

        Assert.NotNull(engine.Parse("SELECT id FROM orders".AsMemory()).Tree);
        Assert.Equal(1, limiter.CurrentCount);
        Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse("SELECT * FROM WHERE".AsMemory()));
        Assert.Equal(1, limiter.CurrentCount);

        limiter.Wait();
        engine.ParseTimeout = TimeSpan.FromMilliseconds(100);
        var ex = Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse("SELECT id FROM orders".AsMemory()));
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public void InlinePath_StatementsAboveTheThreshold_UseThePool()
    {
        var pool = ParseWorkerPool.ForStackSize(IsolatedStackSize + 4096);
        var engine = new FastSqlEngine { ParseThreadStackSize = IsolatedStackSize + 4096, InlineParseMaxLength = 8 };
        int before = pool.ThreadsCreated;

        Assert.NotNull(engine.Parse("SELECT id FROM orders".AsMemory()).Tree);

        Assert.True(pool.ThreadsCreated > before);
    }
}
