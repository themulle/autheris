namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Security;
using TrinoSqlEngine;
using Xunit;

/// <summary>
/// Wunsch 4, Phase 1: COUNT(*) used to become an empty call (<c>"count"()</c>, <c>[COUNT]()</c>) and every function name
/// was quoted like an identifier (<c>"coalesce"(…)</c> does not resolve in PostgreSQL, <c>[SUM](…)</c> not in SQL Server).
/// </summary>
public sealed class FunctionCallEmissionTests
{
    private readonly FastSqlEngine _engine = new();

    private string Generate(string sql, TargetSqlDialect dialect) =>
        _engine.GenerateGovernedSql(sql, new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 't1'"),
            TargetDialect = dialect
        });

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.DuckDb)]
    [InlineData(TargetSqlDialect.Snowflake)]
    public void CountStar_IsEmittedAsCountStar(TargetSqlDialect dialect)
    {
        string sql = Generate("SELECT dept, COUNT(*) AS n FROM orders GROUP BY dept HAVING COUNT(*) > 5", dialect);

        Assert.Contains("COUNT(*)", sql);
        Assert.Contains("HAVING COUNT(*) > 5", sql);
        Assert.DoesNotContain("()", sql.Replace("COUNT(*)", string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void BuiltinFunctions_AreNotQuoted_PostgreSql()
    {
        string sql = Generate("SELECT COALESCE(amount, 0), nullif(dept, 'x'), upper(dept) FROM orders", TargetSqlDialect.PostgreSql);

        Assert.Contains("COALESCE(\"amount\", 0)", sql);
        Assert.Contains("NULLIF(\"dept\", 'x')", sql);
        Assert.Contains("UPPER(\"dept\")", sql);
    }

    [Fact]
    public void BuiltinFunctions_AreNotQuoted_SqlServer()
    {
        string sql = Generate("SELECT SUM(amount), COALESCE(dept, 'none') FROM orders", TargetSqlDialect.SqlServer);

        Assert.Contains("SUM([amount])", sql);
        Assert.Contains("COALESCE([dept], N'none')", sql);
        Assert.DoesNotContain("[SUM]", sql);
        Assert.DoesNotContain("[COALESCE]", sql);
    }

    [Fact]
    public void CastGroupBy_WithCountStar_IsValidSqlServer()
    {
        // Befund: "CAST … GROUP BY" failed on SQL Server because of [COUNT]() in the same statement.
        string sql = Generate(
            "SELECT CAST(created_at AS date) AS d, COUNT(*) AS n FROM orders GROUP BY CAST(created_at AS date)",
            TargetSqlDialect.SqlServer);

        Assert.Contains("COUNT(*) AS [n]", sql);
        Assert.Contains("GROUP BY CAST([created_at] AS date)", sql);
    }

    [Fact]
    public void CountDistinct_KeepsDistinct()
    {
        string sql = Generate("SELECT COUNT(DISTINCT dept) FROM orders", TargetSqlDialect.PostgreSql);

        Assert.Contains("COUNT(DISTINCT \"dept\")", sql);
    }

    [Fact]
    public void QuotedFunctionName_StaysQuoted()
    {
        // A quoted name is the client's explicit choice of a case-sensitive object; it is not upper-cased.
        string sql = Generate("SELECT \"lower\"(dept) FROM orders", TargetSqlDialect.PostgreSql);

        Assert.Contains("\"lower\"(\"dept\")", sql);
    }
}
