namespace TrinoSqlEngine.Tests;

using System;
using Xunit;

public sealed class AstSecurityDmlPrecedenceTests
{
    private static RlsOptions Options(TargetSqlDialect dialect) => new()
    {
        PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 'tenant_1'"),
        TargetDialect = dialect,
        EnforceReadOnlyQueries = false,
        AppendTableAlias = false
    };

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.SqlServer)]
    public void Update_With_User_Or_In_Operand_Emits_Parentheses_Preserving_Rls(TargetSqlDialect dialect)
    {
        var engine = new FastSqlEngine { SqlRewriterEngine = "AstCompiler" };

        var sql = engine.RewriteRls(
            "UPDATE orders SET status = 'cancelled' WHERE (flag1 OR flag2) IN (TRUE)".AsMemory(),
            Options(dialect));

        // The inner binary expression must be wrapped in parentheses: (flag1 OR flag2) IN (...)
        Assert.Matches(@"\((flag1|""flag1""|\[flag1\])\s+OR\s+(flag2|""flag2""|\[flag2\])\)\s+IN", sql);
        Assert.Contains("tenant_id", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.SqlServer)]
    public void Delete_With_User_Or_Condition_Does_Not_Bypass_Tenant_Rls(TargetSqlDialect dialect)
    {
        var engine = new FastSqlEngine { SqlRewriterEngine = "AstCompiler" };

        var sql = engine.RewriteRls(
            "DELETE FROM orders WHERE amount > 10 OR status = 'pending'".AsMemory(),
            Options(dialect));

        // The user condition must be grouped: (amount > 10 OR status = 'pending') AND (tenant_id = ...)
        Assert.Contains("tenant_id", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"\((amount|""amount""|\[amount\])\s*>\s*10\s+OR\s+(status|""status""|\[status\])\s*=\s*N?'pending'\)\s+AND", sql);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.SqlServer)]
    public void Delete_With_Composite_InSubquery_Operand_Emits_Parentheses(TargetSqlDialect dialect)
    {
        var engine = new FastSqlEngine { SqlRewriterEngine = "AstCompiler" };

        var sql = engine.RewriteRls(
            "DELETE FROM orders WHERE (a + b) IN (SELECT id FROM other_orders)".AsMemory(),
            Options(dialect));

        Assert.Matches(@"\((a|""a""|\[a\])\s*\+\s*(b|""b""|\[b\])\)\s+IN", sql);
        Assert.Contains("tenant_id", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.SqlServer)]
    public void Update_With_Composite_Between_Operand_Emits_Parentheses(TargetSqlDialect dialect)
    {
        var engine = new FastSqlEngine { SqlRewriterEngine = "AstCompiler" };

        var sql = engine.RewriteRls(
            "UPDATE orders SET status = 'active' WHERE (a + b) BETWEEN 1 AND 10".AsMemory(),
            Options(dialect));

        Assert.Matches(@"\((a|""a""|\[a\])\s*\+\s*(b|""b""|\[b\])\)\s+BETWEEN", sql);
        Assert.Contains("tenant_id", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Delete_With_IsDistinctFrom_In_Sqlite_Emits_Parentheses_For_Composite_Operands()
    {
        var engine = new FastSqlEngine { SqlRewriterEngine = "AstCompiler" };

        var sql = engine.RewriteRls(
            "DELETE FROM orders WHERE (flag1 OR flag2) IS DISTINCT FROM FALSE".AsMemory(),
            Options(TargetSqlDialect.Sqlite));

        // SQLite formats IS DISTINCT FROM as IS NOT
        Assert.Matches(@"\(""flag1""\s+OR\s+""flag2""\)\s+IS\s+NOT", sql);
        Assert.Contains("tenant_id", sql, StringComparison.OrdinalIgnoreCase);
    }
}
