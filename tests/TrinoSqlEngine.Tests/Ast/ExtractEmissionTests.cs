namespace TrinoSqlEngine.Tests.Ast;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

/// <summary>
/// Wunsch 4, Phase 6a: EXTRACT was always emitted in ANSI form. SQL Server and SQLite have no EXTRACT, and PostgreSQL's
/// DOW counts 0 (Sunday) to 6 while Trino's DOW/DAY_OF_WEEK is ISO, 1 (Monday) to 7 (Sunday).
/// </summary>
public sealed class ExtractEmissionTests
{
    private readonly FastSqlEngine _engine = new();

    private string Generate(string sql, TargetSqlDialect dialect) =>
        _engine.GenerateGovernedSql(sql, new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 't1'"),
            TargetDialect = dialect
        });

    private string Extract(string field, TargetSqlDialect dialect) =>
        Generate($"SELECT EXTRACT({field} FROM created_at) AS v FROM orders", dialect);

    [Theory]
    [InlineData("YEAR", "EXTRACT(YEAR FROM \"created_at\")")]
    [InlineData("DOW", "EXTRACT(ISODOW FROM \"created_at\")")]
    [InlineData("DAY_OF_WEEK", "EXTRACT(ISODOW FROM \"created_at\")")]
    [InlineData("DAY_OF_YEAR", "EXTRACT(DOY FROM \"created_at\")")]
    [InlineData("DAY_OF_MONTH", "EXTRACT(DAY FROM \"created_at\")")]
    [InlineData("YEAR_OF_WEEK", "EXTRACT(ISOYEAR FROM \"created_at\")")]
    [InlineData("WEEK", "EXTRACT(WEEK FROM \"created_at\")")]
    public void PostgreSql(string field, string expected)
    {
        Assert.Contains(expected, Extract(field, TargetSqlDialect.PostgreSql));
    }

    [Theory]
    [InlineData("YEAR", "DATEPART(year, [created_at])")]
    [InlineData("QUARTER", "DATEPART(quarter, [created_at])")]
    [InlineData("MONTH", "DATEPART(month, [created_at])")]
    [InlineData("WEEK", "DATEPART(iso_week, [created_at])")]
    [InlineData("DAY", "DATEPART(day, [created_at])")]
    [InlineData("DAY_OF_YEAR", "DATEPART(dayofyear, [created_at])")]
    [InlineData("HOUR", "DATEPART(hour, [created_at])")]
    [InlineData("SECOND", "DATEPART(second, [created_at])")]
    [InlineData("DOW", "((DATEPART(weekday, [created_at]) + @@DATEFIRST + 5) % 7 + 1)")]
    public void SqlServer(string field, string expected)
    {
        Assert.Contains(expected, Extract(field, TargetSqlDialect.SqlServer));
    }

    [Theory]
    [InlineData("YEAR", "CAST(strftime('%Y', \"created_at\") AS INTEGER)")]
    [InlineData("MONTH", "CAST(strftime('%m', \"created_at\") AS INTEGER)")]
    [InlineData("DAY", "CAST(strftime('%d', \"created_at\") AS INTEGER)")]
    [InlineData("DAY_OF_YEAR", "CAST(strftime('%j', \"created_at\") AS INTEGER)")]
    [InlineData("DOW", "((CAST(strftime('%w', \"created_at\") AS INTEGER) + 6) % 7 + 1)")]
    [InlineData("QUARTER", "((CAST(strftime('%m', \"created_at\") AS INTEGER) + 2) / 3)")]
    public void Sqlite(string field, string expected)
    {
        Assert.Contains(expected, Extract(field, TargetSqlDialect.Sqlite));
    }

    [Theory]
    [InlineData("YEAR", "EXTRACT(YEAR FROM \"CREATED_AT\")")]
    [InlineData("HOUR", "EXTRACT(HOUR FROM CAST(\"CREATED_AT\" AS TIMESTAMP))")]
    [InlineData("QUARTER", "TO_NUMBER(TO_CHAR(\"CREATED_AT\", 'Q'))")]
    [InlineData("WEEK", "TO_NUMBER(TO_CHAR(\"CREATED_AT\", 'IW'))")]
    [InlineData("DAY_OF_YEAR", "TO_NUMBER(TO_CHAR(\"CREATED_AT\", 'DDD'))")]
    public void Oracle(string field, string expected)
    {
        Assert.Contains(expected, Extract(field, TargetSqlDialect.Oracle));
    }

    [Theory]
    [InlineData("DOW", "EXTRACT(DAYOFWEEKISO FROM \"CREATED_AT\")")]
    [InlineData("WEEK", "EXTRACT(WEEKISO FROM \"CREATED_AT\")")]
    public void Snowflake(string field, string expected)
    {
        Assert.Contains(expected, Extract(field, TargetSqlDialect.Snowflake));
    }

    [Theory]
    [InlineData("EPOCH", TargetSqlDialect.SqlServer)]
    [InlineData("YEAR_OF_WEEK", TargetSqlDialect.SqlServer)]
    [InlineData("TIMEZONE_HOUR", TargetSqlDialect.SqlServer)]
    [InlineData("WEEK", TargetSqlDialect.Sqlite)]
    [InlineData("EPOCH", TargetSqlDialect.Sqlite)]
    [InlineData("DOW", TargetSqlDialect.Oracle)]
    public void Unsupported_IsRejected(string field, TargetSqlDialect dialect)
    {
        Assert.Throws<AstBuildException>(() => Extract(field, dialect));
    }
}
