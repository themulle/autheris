namespace TrinoSqlEngine.Tests.Ast;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

/// <summary>
/// Wunsch 4, Phase 5: ROLLUP, CUBE and GROUPING SETS used to remove the whole GROUP BY (silently different results),
/// GROUP BY DISTINCT lost its quantifier and GROUPING(…) failed with 500.
/// </summary>
public sealed class GroupingSetsEmissionTests
{
    private readonly FastSqlEngine _engine = new();

    private string Generate(string sql, TargetSqlDialect dialect) =>
        _engine.GenerateGovernedSql(sql, new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 't1'"),
            TargetDialect = dialect
        });

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "GROUP BY ROLLUP (\"dept\")")]
    [InlineData(TargetSqlDialect.SqlServer, "GROUP BY ROLLUP ([dept])")]
    [InlineData(TargetSqlDialect.Oracle, "GROUP BY ROLLUP (\"DEPT\")")]
    [InlineData(TargetSqlDialect.DuckDb, "GROUP BY ROLLUP (\"dept\")")]
    public void Rollup(TargetSqlDialect dialect, string expected)
    {
        Assert.Contains(expected, Generate("SELECT dept, SUM(amount) FROM orders GROUP BY ROLLUP (dept)", dialect));
    }

    [Fact]
    public void Cube_WithSeveralColumns()
    {
        Assert.Contains("GROUP BY CUBE (\"dept\", \"region\")", Generate("SELECT dept, region, SUM(amount) FROM orders GROUP BY CUBE (dept, region)", TargetSqlDialect.PostgreSql));
    }

    [Fact]
    public void Rollup_WithCompositeSet()
    {
        Assert.Contains("GROUP BY ROLLUP ((\"dept\", \"region\"), \"id\")", Generate("SELECT SUM(amount) FROM orders GROUP BY ROLLUP ((dept, region), id)", TargetSqlDialect.PostgreSql));
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "GROUP BY GROUPING SETS ((\"dept\"), ())")]
    [InlineData(TargetSqlDialect.SqlServer, "GROUP BY GROUPING SETS (([dept]), ())")]
    public void GroupingSets_WithEmptySet(TargetSqlDialect dialect, string expected)
    {
        Assert.Contains(expected, Generate("SELECT dept, SUM(amount) FROM orders GROUP BY GROUPING SETS ((dept), ())", dialect));
    }

    [Fact]
    public void Mixed_SimpleAndRollup()
    {
        Assert.Contains("GROUP BY \"region\", ROLLUP (\"dept\")", Generate("SELECT region, dept, SUM(amount) FROM orders GROUP BY region, ROLLUP (dept)", TargetSqlDialect.PostgreSql));
    }

    [Fact]
    public void GroupByDistinct_PostgreSql()
    {
        Assert.Contains("GROUP BY DISTINCT ROLLUP (\"dept\")", Generate("SELECT dept, SUM(amount) FROM orders GROUP BY DISTINCT ROLLUP (dept)", TargetSqlDialect.PostgreSql));
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Oracle)]
    public void GroupByDistinct_WithoutSupport_IsRejected(TargetSqlDialect dialect)
    {
        Assert.Throws<AstBuildException>(() => Generate("SELECT dept, SUM(amount) FROM orders GROUP BY DISTINCT ROLLUP (dept)", dialect));
    }

    [Theory]
    [InlineData("SELECT dept, SUM(amount) FROM orders GROUP BY ROLLUP (dept)")]
    [InlineData("SELECT dept, SUM(amount) FROM orders GROUP BY CUBE (dept)")]
    [InlineData("SELECT dept, SUM(amount) FROM orders GROUP BY GROUPING SETS ((dept), ())")]
    public void Sqlite_HasNoGroupingSets_IsRejected(string sql)
    {
        Assert.Throws<AstBuildException>(() => Generate(sql, TargetSqlDialect.Sqlite));
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "GROUPING(\"dept\")")]
    [InlineData(TargetSqlDialect.SqlServer, "GROUPING([dept])")]
    public void GroupingOperation_SingleColumn(TargetSqlDialect dialect, string expected)
    {
        Assert.Contains(expected, Generate("SELECT dept, grouping(dept), SUM(amount) FROM orders GROUP BY ROLLUP (dept)", dialect));
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "GROUPING(\"dept\", \"region\")")]
    [InlineData(TargetSqlDialect.SqlServer, "GROUPING_ID([dept], [region])")]
    public void GroupingOperation_SeveralColumns_IsBitmask(TargetSqlDialect dialect, string expected)
    {
        Assert.Contains(expected, Generate("SELECT grouping(dept, region), SUM(amount) FROM orders GROUP BY CUBE (dept, region)", dialect));
    }

    [Fact]
    public void GroupByAuto_IsRejected()
    {
        Assert.Throws<AstBuildException>(() => Generate("SELECT dept, SUM(amount) FROM orders GROUP BY AUTO", TargetSqlDialect.PostgreSql));
    }
}
