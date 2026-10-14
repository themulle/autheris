using Antlr4.Runtime.Misc;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>WP-ARC-C-11 (ARC-S-09): invalid input is bounded in cost and repeated invalid input is rejected from a small negative cache.</summary>
[Trait("Category", "SqlGate")]
public class SyntaxErrorFastPathTests
{
    private const string Invalid = "SELECT * FROM WHERE";

    [Fact]
    public void RepeatedInvalidSql_IsRejectedFromTheNegativeCache_WithTheSameMessage()
    {
        var engine = new FastSqlEngine();

        var first = Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse(Invalid.AsMemory()));
        Assert.Equal(1, engine.SyntaxErrorCacheCount);

        var second = Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse(Invalid.AsMemory()));
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(1, engine.SyntaxErrorCacheCount);
    }

    [Fact]
    public void ValidSql_IsNeverCached()
    {
        var engine = new FastSqlEngine();

        Assert.NotNull(engine.Parse("SELECT id FROM orders".AsMemory()).Tree);
        Assert.NotNull(engine.Parse("SELECT id FROM orders".AsMemory()).Tree);

        Assert.Equal(0, engine.SyntaxErrorCacheCount);
    }

    [Fact]
    public void ZeroTtl_DisablesTheCache()
    {
        var engine = new FastSqlEngine { SyntaxErrorCacheTtl = TimeSpan.Zero };

        Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse(Invalid.AsMemory()));

        Assert.Equal(0, engine.SyntaxErrorCacheCount);
    }

    [Fact]
    public void Cache_IsBounded()
    {
        var engine = new FastSqlEngine { SyntaxErrorCacheMaxEntries = 3 };

        for (int i = 0; i < 10; i++)
        {
            Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse($"SELECT * FROM WHERE x{i}".AsMemory()));
        }

        Assert.True(engine.SyntaxErrorCacheCount <= 3);
    }

    [Fact]
    public void Cache_KeyIncludesTheTokenSecurityOptions()
    {
        var engine = new FastSqlEngine();

        Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse(Invalid.AsMemory(), SqlTokenSecurityOptions.None));
        Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse(Invalid.AsMemory(), SqlTokenSecurityOptions.Strict));

        Assert.Equal(2, engine.SyntaxErrorCacheCount);
    }

    [Fact]
    public void Cache_KeyIncludesTheEntryRule()
    {
        var engine = new FastSqlEngine();

        Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse("1 +".AsMemory()));
        Assert.ThrowsAny<ParseCanceledException>(() => engine.ParseExpression("1 +".AsMemory()));

        Assert.Equal(2, engine.SyntaxErrorCacheCount);
    }

    [Fact]
    public void Timeouts_AreNotCached()
    {
        var engine = new FastSqlEngine { ParseTimeout = TimeSpan.FromTicks(1) };
        string sql = "SELECT * FROM orders WHERE id IN (" + string.Join(", ", Enumerable.Range(0, 5000)) + ")";

        var ex = Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse(sql.AsMemory()));

        Assert.IsType<TimeoutException>(ex.InnerException);
        Assert.Equal(0, engine.SyntaxErrorCacheCount);
    }

    [Fact]
    public void LlFallbackBudget_BoundsTheFallbackPass_AndIsNotCached()
    {
        // A budget so small that the LL pass (when one is needed) can only fail by timeout; a valid statement that SLL accepts is unaffected.
        var engine = new FastSqlEngine { LlFallbackTimeout = TimeSpan.FromTicks(1), SkipLlFallbackOnTokenMismatch = false };

        Assert.NotNull(engine.Parse("SELECT id FROM orders WHERE id > 1".AsMemory()).Tree);
        var ex = Assert.ThrowsAny<ParseCanceledException>(() => engine.Parse(Invalid.AsMemory()));

        // Either the statement failed already in the SLL pass or the LL pass ran out of budget; both are rejections, and a budget
        // timeout is never remembered as a syntax error.
        if (ex.InnerException is TimeoutException)
        {
            Assert.Equal(0, engine.SyntaxErrorCacheCount);
        }
    }

    [Fact]
    public void TokenMismatch_SkippingLl_StillRejects_AndKeepsTheLineAndColumnFormat()
    {
        var skipping = new FastSqlEngine { SkipLlFallbackOnTokenMismatch = true };
        var retrying = new FastSqlEngine { SkipLlFallbackOnTokenMismatch = false };

        foreach (var sql in new[] { Invalid, "SELECT id FROM orders ORDER", "SELEC id FROM orders" })
        {
            var a = Assert.ThrowsAny<ParseCanceledException>(() => skipping.Parse(sql.AsMemory()));
            var b = Assert.ThrowsAny<ParseCanceledException>(() => retrying.Parse(sql.AsMemory()));
            Assert.StartsWith("line ", a.Message, StringComparison.Ordinal);
            Assert.StartsWith("line ", b.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ValidStatements_ParseIdentically_WithAndWithoutTheTokenMismatchShortcut()
    {
        var skipping = new FastSqlEngine { SkipLlFallbackOnTokenMismatch = true };
        var retrying = new FastSqlEngine { SkipLlFallbackOnTokenMismatch = false };

        foreach (var sql in new[]
        {
            "SELECT a, b FROM t WHERE a IN (1, 2, 3) AND b LIKE 'x%'",
            "SELECT CAST(a AS varchar) FROM t ORDER BY a DESC LIMIT 10",
            "WITH c AS (SELECT 1 AS x) SELECT x FROM c"
        })
        {
            Assert.Equal(
                retrying.Parse(sql.AsMemory()).Tree.ToStringTree(),
                skipping.Parse(sql.AsMemory()).Tree.ToStringTree());
        }
    }
}
