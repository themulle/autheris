namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Reflection;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

/// <summary>#333: quarter for <c>date_trunc</c> only, and the SQL Server hour/week/quarter truncations (SEC-PGP-04, SEC-PGP-05).</summary>
[Trait("Category", "SqlGate")]
public sealed class DateTruncQuarterTests
{
    private static string Generate(string expression, TargetSqlDialect dialect) =>
        AstEmit.Inline($"SELECT id FROM orders WHERE tenant_id = 't1' AND ts >= {expression}", dialect, translateDates: true);

    [Fact]
    public void DateTrunc_Quarter_PostgreSql() =>
        Assert.Contains("DATE_TRUNC('quarter', \"ts\")", Generate("date_trunc('quarter', ts)", TargetSqlDialect.PostgreSql));

    [Fact]
    public void DateTrunc_Quarter_SqlServer() =>
        Assert.Contains("CAST(DATEFROMPARTS(YEAR([ts]), (MONTH([ts]) - 1) / 3 * 3 + 1, 1) AS datetimeoffset)", Generate("date_trunc('quarter', ts)", TargetSqlDialect.SqlServer));

    [Fact]
    public void DateTrunc_Quarter_Oracle() =>
        Assert.Contains("TRUNC(\"TS\", 'Q')", Generate("date_trunc('quarter', ts)", TargetSqlDialect.Oracle));

    [Theory]
    [InlineData("hour", "CAST(DATEADD(hour, DATEPART(hour, [ts]), CAST(CAST([ts] AS date) AS datetime2)) AS datetimeoffset)")]
    [InlineData("week", "CAST(DATEADD(day, -((DATEDIFF(day, CAST('19000101' AS date), CAST([ts] AS date)) % 7 + 7) % 7), CAST([ts] AS date)) AS datetimeoffset)")]
    public void DateTrunc_HourWeek_SqlServer(string unit, string expected) =>
        Assert.Contains(expected, Generate($"date_trunc('{unit}', ts)", TargetSqlDialect.SqlServer));

    [Theory]
    [InlineData("hour")]
    [InlineData("week")]
    [InlineData("quarter")]
    [Trait("Category", "Security")]
    public void SEC_PGP_05_SqlServerDateTrunc_NoSessionDependentConstructs(string unit)
    {
        string sql = Generate($"date_trunc('{unit}', ts)", TargetSqlDialect.SqlServer);

        Assert.DoesNotContain("@@DATEFIRST", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SET LANGUAGE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FORMAT(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CONVERT(", sql, StringComparison.OrdinalIgnoreCase);
        if (unit == "week")
        {
            Assert.Contains("CAST('19000101' AS date)", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DateTrunc_Quarter_Sqlite_ThrowsAstBuild() =>
        Assert.Throws<AstBuildException>(() => Generate("date_trunc('quarter', ts)", TargetSqlDialect.Sqlite));

    [Theory]
    [InlineData("date_add('quarter', 1, ts)")]
    [Trait("Category", "Security")]
    public void SEC_PGP_04_DateAdd_Quarter_StillRejected(string expression) =>
        Assert.Throws<AstBuildException>(() => Generate(expression, TargetSqlDialect.PostgreSql));

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_PGP_04_DateTrunc_Quarter_UnsupportedDialect_Is4xx_NoUnitTextToProvider()
    {
        // SQLite cannot truncate to a quarter: the compile fails with AstBuildException (mapped to 4xx), no SQL with the unit text exists.
        var ex = Assert.Throws<AstBuildException>(() => Generate("date_trunc('quarter', ts)", TargetSqlDialect.Sqlite));
        Assert.DoesNotContain("strftime", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string UnitName(DateUnit unit)
    {
        var method = typeof(SqlDialectGeneratorBase).GetMethod("DateUnitName", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            return (string)method.Invoke(null, [unit])!;
        }
        catch (TargetInvocationException e)
        {
            throw e.InnerException!;
        }
    }

    [Fact]
    public void DateUnitName_CoversEveryEnumValue()
    {
        foreach (var unit in Enum.GetValues<DateUnit>())
        {
            Assert.Equal(unit.ToString().ToLowerInvariant(), UnitName(unit));
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_PGP_04_DateUnitName_NoDefaultFallthrough_ThrowsOnUnknownEnum() =>
        Assert.Throws<AstBuildException>(() => UnitName((DateUnit)999));
}
