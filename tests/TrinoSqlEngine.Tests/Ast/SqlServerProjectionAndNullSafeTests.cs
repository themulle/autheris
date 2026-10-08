namespace TrinoSqlEngine.Tests.Ast;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

/// <summary>
/// Wunsch 4, Phase 7: SQL Server wrapped CASE conditions a second time (<c>CASE WHEN CASE WHEN …</c>) and stopped wrapping
/// boolean projections after a scalar subquery; IS DISTINCT FROM and JOIN … USING were emitted although T-SQL lacks them.
/// </summary>
public sealed class SqlServerProjectionAndNullSafeTests
{
    private readonly FastSqlEngine _engine = new();

    private string Generate(string sql, TargetSqlDialect dialect) =>
        _engine.GenerateGovernedSql(sql, new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 't1'"),
            TargetDialect = dialect
        });

    [Fact]
    public void Case_InProjection_IsNotWrappedTwice()
    {
        string sql = Generate("SELECT CASE WHEN amount > 1 THEN 'x' END AS c, amount > 2 AS f FROM orders", TargetSqlDialect.SqlServer);

        Assert.Contains("CASE WHEN [amount] > 1 THEN N'x' END AS [c]", sql);
        Assert.Contains("CASE WHEN [amount] > 2 THEN 1 ELSE 0 END AS [f]", sql);
        Assert.DoesNotContain("CASE WHEN CASE WHEN", sql);
    }

    [Fact]
    public void BooleanProjection_AfterScalarSubquery_IsStillWrapped()
    {
        string sql = Generate("SELECT (SELECT MAX(id) FROM customers) AS m, amount > 2 AS f FROM orders", TargetSqlDialect.SqlServer);

        Assert.Contains("CASE WHEN [amount] > 2 THEN 1 ELSE 0 END AS [f]", sql);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "\"a\" IS DISTINCT FROM \"b\"", "\"a\" IS NOT DISTINCT FROM \"b\"")]
    [InlineData(TargetSqlDialect.SqlServer, "NOT EXISTS (SELECT [a] INTERSECT SELECT [b])", "EXISTS (SELECT [a] INTERSECT SELECT [b])")]
    [InlineData(TargetSqlDialect.Sqlite, "\"a\" IS NOT \"b\"", "\"a\" IS \"b\"")]
    [InlineData(TargetSqlDialect.Oracle, "DECODE(\"A\", \"B\", 0, 1) = 1", "DECODE(\"A\", \"B\", 0, 1) = 0")]
    public void IsDistinctFrom(TargetSqlDialect dialect, string distinct, string notDistinct)
    {
        Assert.Contains(distinct, Generate("SELECT id FROM orders WHERE a IS DISTINCT FROM b", dialect));
        Assert.Contains(notDistinct, Generate("SELECT id FROM orders WHERE a IS NOT DISTINCT FROM b", dialect));
    }

    [Fact]
    public void JoinUsing_SqlServer_IsRejected()
    {
        Assert.Throws<AstBuildException>(() => Generate("SELECT o.id FROM orders o JOIN customers c USING (id)", TargetSqlDialect.SqlServer));
    }

    [Fact]
    public void JoinUsing_PostgreSql_IsKept()
    {
        Assert.Contains("USING (\"id\")", Generate("SELECT o.id FROM orders o JOIN customers c USING (id)", TargetSqlDialect.PostgreSql));
    }
}
