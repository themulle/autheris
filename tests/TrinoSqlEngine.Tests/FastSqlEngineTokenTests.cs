namespace TrinoSqlEngine.Tests;

using Antlr4.Runtime.Misc;
using TrinoSqlEngine;
using TrinoSqlEngine.Tests.Security;
using Xunit;

/// <summary>#296 GAP-005 (SEC-PG-08): rejected literals are a typed, documented rejection; nothing rejected before is accepted now.</summary>
[Trait("Category", "SqlGate")]
public class FastSqlEngineTokenTests
{
    public static TheoryData<TargetSqlDialect, string> Literals => new()
    {
        { TargetSqlDialect.Sqlite, "SELECT 'a--b' FROM orders" },
        { TargetSqlDialect.Sqlite, "SELECT 'a/*b' FROM orders" },
        { TargetSqlDialect.Sqlite, "SELECT 'a[b' FROM orders" },
        { TargetSqlDialect.Sqlite, "SELECT 'a]b' FROM orders" },
        { TargetSqlDialect.SqlServer, "SELECT 'a--b' FROM orders" },
        { TargetSqlDialect.SqlServer, "SELECT \"a[b\" FROM orders" },
        { TargetSqlDialect.PostgreSql, "SELECT 'a\\b' FROM orders" },
        { TargetSqlDialect.Sqlite, "SELECT 'a\\b' FROM orders" },
        { TargetSqlDialect.SqlServer, "SELECT x FROM orders WHERE y = 'q\\--'" }
    };

    // Exactly ParseCanceledException, as before: callers and tests that match the type keep working; the typed cause is the inner exception.
    private static ParseCanceledException Reject(TargetSqlDialect dialect, string sql) =>
        Assert.Throws<ParseCanceledException>(() => new ProbeEnv(dialect).Compile(sql));

    [Theory]
    [MemberData(nameof(Literals))]
    public void CommentMarkerInLiteral_ThrowsLiteralTokenRejected(TargetSqlDialect dialect, string sql)
    {
        var ex = Reject(dialect, sql);
        Assert.IsType<LiteralTokenRejectedException>(ex.InnerException);
    }

    [Theory]
    [MemberData(nameof(Literals))]
    [Trait("Category", "Security")]
    public void LiteralTokenMessage_DoesNotEchoLiteral(TargetSqlDialect dialect, string sql)
    {
        var ex = Reject(dialect, sql);
        foreach (var text in new[] { ex.Message, ex.InnerException!.Message })
        {
            foreach (var fragment in new[] { "a--b", "a/*b", "a[b", "a]b", "a\\b", "q\\--" })
            {
                Assert.DoesNotContain(fragment, text, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData(TargetSqlDialect.Sqlite, "SELECT [id] FROM orders")]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT ARRAY[1,2][1] FROM orders")]
    [InlineData(TargetSqlDialect.PostgreSql, "SELECT U&'d!0061t' UESCAPE '!' FROM orders")]
    [InlineData(TargetSqlDialect.Sqlite, "SELECT x FROM orders -- comment")]
    [InlineData(TargetSqlDialect.Sqlite, "SELECT x FROM orders /* a /* b */")]
    [InlineData(TargetSqlDialect.PostgreSql, "SELECT $$x$$ FROM orders")]
    [Trait("Category", "Security")]
    public void OtherParseCanceled_StaysSyntaxError(TargetSqlDialect dialect, string sql)
    {
        var ex = Assert.ThrowsAny<ParseCanceledException>(() => new ProbeEnv(dialect).Compile(sql));
        Assert.IsNotType<LiteralTokenRejectedException>(ex.InnerException);
    }

    [Theory]
    [MemberData(nameof(Literals))]
    [InlineData(TargetSqlDialect.Sqlite, "SELECT [id] FROM orders")]
    [InlineData(TargetSqlDialect.Sqlite, "SELECT '[' || x || ']' FROM orders")]
    [InlineData(TargetSqlDialect.SqlServer, "SELECT 'a/*b' FROM orders")]
    [InlineData(TargetSqlDialect.PostgreSql, "SELECT E'a' FROM orders")]
    [Trait("Category", "Security")]
    public void LiteralTokenRejected_RegressionCorpusStillRejected(TargetSqlDialect dialect, string sql)
    {
        // SEC-PG-08: the typed rejection is the same ParseCanceledException as before; nothing rejected before compiles now.
        Assert.Throws<ParseCanceledException>(() => new ProbeEnv(dialect).Compile(sql));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_PG_08_PlainLiterals_StillCompile()
    {
        var compiled = new ProbeEnv(TargetSqlDialect.Sqlite).Compile("SELECT 'abc' FROM orders");
        Assert.Contains(compiled.Parameters, p => Equals(p.Value, "abc"));
    }
}
