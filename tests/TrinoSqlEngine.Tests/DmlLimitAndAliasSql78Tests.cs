namespace TrinoSqlEngine.Tests;

using System;
using Xunit;

/// <summary>
/// SQL-7: the enforced row limit must not end up in a subquery of a DML statement (it would change which rows are
/// written). SQL-8: the alias of a rewritten table keeps the quoting of the written name.
/// </summary>
public sealed class DmlLimitAndAliasSql78Tests
{
    private static RlsOptions Options(TargetSqlDialect dialect) => new()
    {
        PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 'a'"),
        TargetDialect = dialect,
        EnforcedMaxRows = 100,
        EnforceReadOnlyQueries = false,
        AppendTableAlias = true
    };

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.SqlServer)]
    public void AstCompiler_DmlSubquery_HasNoRowLimit(TargetSqlDialect dialect)
    {
        var engine = new FastSqlEngine { SqlRewriterEngine = "AstCompiler" };

        var sql = engine.RewriteRls("DELETE FROM orders WHERE id IN (SELECT id FROM orders o WHERE o.amount > 3)".AsMemory(), Options(dialect));

        Assert.DoesNotContain("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FETCH NEXT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tenant_id", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AstCompiler_RootSelect_StillHasRowLimit()
    {
        var engine = new FastSqlEngine { SqlRewriterEngine = "AstCompiler" };

        var sql = engine.RewriteRls("SELECT id FROM orders".AsMemory(), Options(TargetSqlDialect.PostgreSql));

        Assert.Contains("LIMIT 100", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "AS \"Orders\"")]
    [InlineData(TargetSqlDialect.Sqlite, "AS \"Orders\"")]
    [InlineData(TargetSqlDialect.SqlServer, "AS [Orders]")]
    public void Legacy_QuotedTableName_KeepsQuotedAlias(TargetSqlDialect dialect, string expectedAlias)
    {
        var engine = new FastSqlEngine { SqlRewriterEngine = "Legacy" };

        var sql = engine.RewriteRls("SELECT \"Orders\".id FROM \"Orders\"".AsMemory(), Options(dialect));

        Assert.Contains(expectedAlias, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_UnquotedTableName_KeepsUnquotedAlias()
    {
        var engine = new FastSqlEngine { SqlRewriterEngine = "Legacy" };

        var sql = engine.RewriteRls("SELECT orders.id FROM orders".AsMemory(), Options(TargetSqlDialect.PostgreSql));

        Assert.Contains("AS orders", sql, StringComparison.Ordinal);
    }
}
