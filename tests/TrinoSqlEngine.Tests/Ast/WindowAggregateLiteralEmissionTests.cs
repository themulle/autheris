namespace TrinoSqlEngine.Tests.Ast;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

/// <summary>
/// Wunsch 4, Phase 4: constructs the AST compiler used to drop silently (window ORDER BY and frames, FILTER, ORDER BY inside
/// aggregates, typed literals). Each is now emitted per dialect or rejected with <see cref="AstBuildException"/>.
/// </summary>
public sealed class WindowAggregateLiteralEmissionTests
{
    private readonly FastSqlEngine _engine = new();

    private string Generate(string sql, TargetSqlDialect dialect) =>
        _engine.GenerateGovernedSql(sql, new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 't1'"),
            TargetDialect = dialect
        });

    // ---- window ORDER BY (was: OVER () or InvalidCastException) ----

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "ROW_NUMBER() OVER (PARTITION BY \"dept\" ORDER BY \"amount\" DESC)")]
    [InlineData(TargetSqlDialect.SqlServer, "ROW_NUMBER() OVER (PARTITION BY [dept] ORDER BY [amount] DESC)")]
    [InlineData(TargetSqlDialect.Sqlite, "ROW_NUMBER() OVER (PARTITION BY \"dept\" ORDER BY \"amount\" DESC)")]
    public void Window_OrderBy_IsKept(TargetSqlDialect dialect, string expected)
    {
        Assert.Contains(expected, Generate("SELECT row_number() OVER (PARTITION BY dept ORDER BY amount DESC) FROM orders", dialect));
    }

    [Fact]
    public void Window_OrderBy_WithoutDirection_Builds()
    {
        Assert.Contains("RANK() OVER (ORDER BY \"amount\" ASC)", Generate("SELECT rank() OVER (ORDER BY amount) FROM orders", TargetSqlDialect.PostgreSql));
    }

    // ---- window frames ----

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.Oracle)]
    public void Window_RowsFrame_IsKept(TargetSqlDialect dialect)
    {
        string sql = Generate("SELECT SUM(amount) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM orders", dialect);

        Assert.Contains("ROWS BETWEEN 1 PRECEDING AND CURRENT ROW)", sql);
    }

    [Fact]
    public void Window_RangeUnboundedFrame_IsKept()
    {
        string sql = Generate("SELECT SUM(amount) OVER (ORDER BY id RANGE BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) FROM orders", TargetSqlDialect.PostgreSql);

        Assert.Contains("RANGE BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING)", sql);
    }

    [Fact]
    public void Window_GroupsFrame_IsRejected()
    {
        Assert.Throws<AstBuildException>(() => Generate("SELECT SUM(amount) OVER (ORDER BY id GROUPS 1 PRECEDING) FROM orders", TargetSqlDialect.PostgreSql));
    }

    [Fact]
    public void Window_FrameOffset_MustBeIntegerLiteral()
    {
        Assert.Throws<AstBuildException>(() => Generate("SELECT SUM(amount) OVER (ORDER BY id ROWS BETWEEN (SELECT 1) PRECEDING AND CURRENT ROW) FROM orders", TargetSqlDialect.PostgreSql));
    }

    // ---- FILTER (was: dropped, the aggregate ran over all rows) ----

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "COUNT(*) FILTER (WHERE \"amount\" > 100)")]
    [InlineData(TargetSqlDialect.Sqlite, "COUNT(*) FILTER (WHERE \"amount\" > 100)")]
    [InlineData(TargetSqlDialect.DuckDb, "COUNT(*) FILTER (WHERE \"amount\" > 100)")]
    [InlineData(TargetSqlDialect.SqlServer, "COUNT(CASE WHEN [amount] > 100 THEN 1 END)")]
    [InlineData(TargetSqlDialect.Oracle, "COUNT(CASE WHEN \"AMOUNT\" > 100 THEN 1 END)")]
    public void Filter_CountStar(TargetSqlDialect dialect, string expected)
    {
        Assert.Contains(expected, Generate("SELECT COUNT(*) FILTER (WHERE amount > 100) FROM orders", dialect));
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "SUM(DISTINCT \"amount\") FILTER (WHERE \"dept\" = 'x')")]
    [InlineData(TargetSqlDialect.SqlServer, "SUM(DISTINCT CASE WHEN [dept] = N'x' THEN [amount] END)")]
    public void Filter_WithArgument(TargetSqlDialect dialect, string expected)
    {
        Assert.Contains(expected, Generate("SELECT SUM(DISTINCT amount) FILTER (WHERE dept = 'x') FROM orders", dialect));
    }

    [Fact]
    public void Filter_OnNonAggregateWithSeveralArguments_IsRejectedWhereEmulated()
    {
        Assert.Throws<AstBuildException>(() => Generate("SELECT max_by(id, amount) FILTER (WHERE dept = 'x') FROM orders", TargetSqlDialect.SqlServer));
    }

    // ---- ORDER BY inside aggregates (was: dropped) ----

    [Fact]
    public void OrderedAggregate_PostgreSql_IsKept()
    {
        Assert.Contains("ARRAY_AGG(\"id\" ORDER BY \"amount\" ASC)", Generate("SELECT array_agg(id ORDER BY amount) FROM orders", TargetSqlDialect.PostgreSql));
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.Oracle)]
    public void OrderedAggregate_WithoutNativeSupport_IsRejected(TargetSqlDialect dialect)
    {
        Assert.Throws<AstBuildException>(() => Generate("SELECT array_agg(id ORDER BY amount) FROM orders", dialect));
    }

    // ---- typed literals (was: 'DATE''2024-01-01''') ----

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "> DATE '2024-01-01'")]
    [InlineData(TargetSqlDialect.Oracle, "> DATE '2024-01-01'")]
    [InlineData(TargetSqlDialect.SqlServer, "> CAST(N'2024-01-01' AS date)")]
    [InlineData(TargetSqlDialect.Sqlite, "> '2024-01-01'")]
    public void DateLiteral(TargetSqlDialect dialect, string expected)
    {
        Assert.Contains(expected, Generate("SELECT id FROM orders WHERE created_at > DATE '2024-01-01'", dialect));
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "TIMESTAMP '2024-01-01 10:00:00'")]
    [InlineData(TargetSqlDialect.SqlServer, "CAST(N'2024-01-01 10:00:00' AS datetime2)")]
    [InlineData(TargetSqlDialect.Sqlite, "'2024-01-01 10:00:00'")]
    public void TimestampLiteral(TargetSqlDialect dialect, string expected)
    {
        Assert.Contains(expected, Generate("SELECT id FROM orders WHERE created_at < TIMESTAMP '2024-01-01 10:00:00'", dialect));
    }

    [Theory]
    [InlineData("SELECT id FROM orders WHERE created_at > DATE 'not-a-date'")]
    [InlineData("SELECT id FROM orders WHERE created_at > JSON '{}'")]
    public void TypedLiteral_InvalidOrUnsupported_IsRejected(string sql)
    {
        Assert.Throws<AstBuildException>(() => Generate(sql, TargetSqlDialect.PostgreSql));
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "+ INTERVAL '1' DAY")]
    [InlineData(TargetSqlDialect.Oracle, "+ INTERVAL '1' DAY")]
    public void IntervalLiteral(TargetSqlDialect dialect, string expected)
    {
        Assert.Contains(expected, Generate("SELECT created_at + INTERVAL '1' DAY FROM orders", dialect));
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Sqlite)]
    public void IntervalLiteral_WithoutIntervalType_IsRejected(TargetSqlDialect dialect)
    {
        Assert.Throws<AstBuildException>(() => Generate("SELECT created_at + INTERVAL '1' DAY FROM orders", dialect));
    }
}
