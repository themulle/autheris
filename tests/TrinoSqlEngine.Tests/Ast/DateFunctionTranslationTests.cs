namespace TrinoSqlEngine.Tests.Ast;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

/// <summary>
/// Virtual filters, phase 7b: filters are written in Trino syntax. With the date function translation of the request
/// the AST compiler translates <c>date_add</c>, <c>date_trunc</c>, <c>now()</c> and <c>x ± INTERVAL</c> into each dialect's
/// date arithmetic. Without the option (WebSQL) nothing changes.
/// </summary>
[Trait("Category", "SqlGate")]
public sealed class DateFunctionTranslationTests
{
    private string Generate(string expression, TargetSqlDialect dialect, bool translate = true) =>
        AstEmit.Inline($"SELECT id FROM orders WHERE tenant_id = 't1' AND ts >= {expression}", dialect, translateDates: translate);

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "DATEADD(day, -1, SYSDATETIMEOFFSET())")]
    [InlineData(TargetSqlDialect.PostgreSql, "(CURRENT_TIMESTAMP + (-1) * INTERVAL '1 day')")]
    [InlineData(TargetSqlDialect.Oracle, "(CURRENT_TIMESTAMP + NUMTODSINTERVAL(-1, 'DAY'))")]
    [InlineData(TargetSqlDialect.DuckDb, "(CURRENT_TIMESTAMP + INTERVAL '-1 day')")]
    [InlineData(TargetSqlDialect.Ansi, "(CURRENT_TIMESTAMP - INTERVAL '1' DAY)")]
    public void DateAdd_Day(TargetSqlDialect dialect, string expected)
    {
        Assert.Contains(expected, Generate("date_add('day', -1, current_timestamp)", dialect));
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "month", 3, "DATEADD(month, 3, [ts])")]
    [InlineData(TargetSqlDialect.SqlServer, "week", 2, "DATEADD(week, 2, [ts])")]
    [InlineData(TargetSqlDialect.PostgreSql, "hour", 6, "(\"ts\" + (6) * INTERVAL '1 hour')")]
    [InlineData(TargetSqlDialect.PostgreSql, "week", 1, "(\"ts\" + (1) * INTERVAL '1 week')")]
    [InlineData(TargetSqlDialect.Oracle, "month", 2, "ADD_MONTHS(\"TS\", 2)")]
    [InlineData(TargetSqlDialect.Oracle, "year", -1, "ADD_MONTHS(\"TS\", -12)")]
    [InlineData(TargetSqlDialect.Oracle, "week", 1, "(\"TS\" + NUMTODSINTERVAL(7, 'DAY'))")]
    [InlineData(TargetSqlDialect.Ansi, "week", 1, "(\"ts\" + INTERVAL '7' DAY)")]
    public void DateAdd_Units(TargetSqlDialect dialect, string unit, int amount, string expected)
    {
        Assert.Contains(expected, Generate($"date_add('{unit}', {amount}, ts)", dialect));
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "day", "CAST(CAST([ts] AS date) AS datetimeoffset)")]
    [InlineData(TargetSqlDialect.SqlServer, "month", "CAST(DATEFROMPARTS(YEAR([ts]), MONTH([ts]), 1) AS datetimeoffset)")]
    [InlineData(TargetSqlDialect.SqlServer, "year", "CAST(DATEFROMPARTS(YEAR([ts]), 1, 1) AS datetimeoffset)")]
    [InlineData(TargetSqlDialect.PostgreSql, "day", "DATE_TRUNC('day', \"ts\")")]
    [InlineData(TargetSqlDialect.PostgreSql, "week", "DATE_TRUNC('week', \"ts\")")]
    [InlineData(TargetSqlDialect.Oracle, "day", "TRUNC(\"TS\", 'DD')")]
    [InlineData(TargetSqlDialect.Oracle, "week", "TRUNC(\"TS\", 'IW')")]
    [InlineData(TargetSqlDialect.DuckDb, "day", "DATE_TRUNC('day', \"ts\")")]
    public void DateTrunc(TargetSqlDialect dialect, string unit, string expected)
    {
        Assert.Contains(expected, Generate($"date_trunc('{unit}', ts)", dialect));
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "second")]
    [InlineData(TargetSqlDialect.Oracle, "second")]
    public void DateTrunc_UnitTheDialectCannotTruncate_IsRejectedNamingDialectAndFunction(TargetSqlDialect dialect, string unit)
    {
        var ex = Assert.Throws<AstBuildException>(() => Generate($"date_trunc('{unit}', ts)", dialect));
        Assert.Contains("date_trunc", ex.Message, StringComparison.Ordinal);
        Assert.Contains(dialect.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer, "SYSDATETIMEOFFSET()")]
    [InlineData(TargetSqlDialect.PostgreSql, "CURRENT_TIMESTAMP")]
    public void Now_IsCurrentTimestamp(TargetSqlDialect dialect, string expected)
    {
        string sql = Generate("now()", dialect);
        Assert.Contains(expected, sql);
        Assert.DoesNotContain("NOW(", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("current_timestamp - interval '1' day", TargetSqlDialect.SqlServer, "DATEADD(day, -1, SYSDATETIMEOFFSET())")]
    [InlineData("ts - interval '3' month", TargetSqlDialect.PostgreSql, "(\"ts\" + (-3) * INTERVAL '1 month')")]
    [InlineData("interval '1' year + ts", TargetSqlDialect.SqlServer, "DATEADD(year, 1, [ts])")]
    public void IntervalArithmetic_BecomesDateAdd(string expression, TargetSqlDialect dialect, string expected)
    {
        Assert.Contains(expected, Generate(expression, dialect));
    }

    [Theory]
    [InlineData("date_add('fortnight', 1, ts)")]
    [InlineData("date_add(unit_col, 1, ts)")]
    [InlineData("date_add('day', 1.5, ts)")]
    [InlineData("date_add('day', n, ts)")]
    [InlineData("date_add('day', 1)")]
    [InlineData("date_trunc('fortnight', ts)")]
    [InlineData("date_trunc(unit_col, ts)")]
    [InlineData("date_diff('day', ts, current_timestamp)")]
    [InlineData("now(3)")]
    [InlineData("interval '1' day - ts")]
    public void InvalidUsage_IsRejected(string expression)
    {
        Assert.Throws<AstBuildException>(() => Generate(expression, TargetSqlDialect.PostgreSql));
    }

    [Fact]
    public void WithoutTheOption_DateFunctionsAreUnchanged()
    {
        string sql = Generate("date_add('day', -1, current_timestamp)", TargetSqlDialect.SqlServer, translate: false);
        Assert.Contains("DATE_ADD(", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DATEADD(", sql, StringComparison.Ordinal);

        // INTERVAL arithmetic keeps failing on SQL Server as before (no interval type).
        Assert.Throws<AstBuildException>(() => Generate("current_timestamp - interval '1' day", TargetSqlDialect.SqlServer, translate: false));
    }

    [Fact]
    public void WithoutTheOption_NowStaysAFunctionCall()
    {
        Assert.Contains("NOW()", Generate("now()", TargetSqlDialect.PostgreSql, translate: false), StringComparison.Ordinal);
    }
}
