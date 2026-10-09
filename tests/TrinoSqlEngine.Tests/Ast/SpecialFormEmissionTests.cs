namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Collections.Generic;
using System.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

/// <summary>
/// Wunsch 4, Phase 6b: SQL special forms without a builder visitor (current_date …, substring … FROM, trim, position)
/// failed with an ArgumentNullException (500). They are now translated per dialect.
/// </summary>
public sealed class SpecialFormEmissionTests
{
    private readonly FastSqlEngine _engine = new();

    private string Generate(string sql, TargetSqlDialect dialect, RlsOptions? options = null)
    {
        options ??= new RlsOptions();
        options.PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 't1'");
        options.TargetDialect = dialect;
        return _engine.GenerateGovernedSql(sql, options);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "current_date", "CURRENT_DATE")]
    [InlineData(TargetSqlDialect.PostgreSql, "current_timestamp", "CURRENT_TIMESTAMP")]
    [InlineData(TargetSqlDialect.PostgreSql, "localtimestamp", "LOCALTIMESTAMP")]
    [InlineData(TargetSqlDialect.SqlServer, "current_date", "CAST(SYSDATETIME() AS date)")]
    [InlineData(TargetSqlDialect.SqlServer, "current_timestamp", "SYSDATETIMEOFFSET()")]
    [InlineData(TargetSqlDialect.SqlServer, "localtimestamp", "SYSDATETIME()")]
    [InlineData(TargetSqlDialect.Sqlite, "current_date", "CURRENT_DATE")]
    [InlineData(TargetSqlDialect.Sqlite, "localtimestamp", "datetime('now', 'localtime')")]
    [InlineData(TargetSqlDialect.Oracle, "current_date", "TRUNC(CURRENT_DATE)")]
    public void CurrentDateTime(TargetSqlDialect dialect, string function, string expected)
    {
        Assert.Contains($"> {expected}", Generate($"SELECT id FROM orders WHERE created_at > {function}", dialect));
    }

    [Theory]
    [InlineData("SELECT id FROM orders WHERE created_at > current_timestamp(3)")]
    [InlineData("SELECT current_user FROM orders")]
    public void CurrentDateTime_WithPrecisionOrSessionInfo_IsRejected(string sql)
    {
        Assert.Throws<AstBuildException>(() => Generate(sql, TargetSqlDialect.PostgreSql));
    }

    [Fact]
    public void CurrentTime_Oracle_IsRejected()
    {
        Assert.Throws<AstBuildException>(() => Generate("SELECT id FROM orders WHERE created_at > current_time", TargetSqlDialect.Oracle));
    }

    [Fact]
    public void IntervalArithmetic_OnCurrentDate_PostgreSql()
    {
        Assert.Contains("> CURRENT_DATE - INTERVAL '7' DAY", Generate("SELECT id FROM orders WHERE created_at > current_date - INTERVAL '7' DAY", TargetSqlDialect.PostgreSql));
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "SUBSTRING(\"name\", 2, 3)", "SUBSTRING(\"name\", 2)")]
    [InlineData(TargetSqlDialect.SqlServer, "SUBSTRING([name], 2, 3)", "SUBSTRING([name], 2, 2147483647)")]
    [InlineData(TargetSqlDialect.Sqlite, "SUBSTR(\"name\", 2, 3)", "SUBSTR(\"name\", 2)")]
    [InlineData(TargetSqlDialect.Oracle, "SUBSTR(\"NAME\", 2, 3)", "SUBSTR(\"NAME\", 2)")]
    public void Substring_FromFor(TargetSqlDialect dialect, string withLength, string withoutLength)
    {
        Assert.Contains(withLength, Generate("SELECT substring(name FROM 2 FOR 3) FROM orders", dialect));
        Assert.Contains(withoutLength, Generate("SELECT substring(name FROM 2) FROM orders", dialect));
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "trim(name)", "TRIM(BOTH FROM \"name\")")]
    [InlineData(TargetSqlDialect.PostgreSql, "trim(LEADING 'x' FROM name)", "TRIM(LEADING 'x' FROM \"name\")")]
    [InlineData(TargetSqlDialect.PostgreSql, "trim(name, 'x')", "TRIM(BOTH 'x' FROM \"name\")")]
    [InlineData(TargetSqlDialect.SqlServer, "trim(name)", "TRIM([name])")]
    [InlineData(TargetSqlDialect.SqlServer, "trim(LEADING FROM name)", "LTRIM([name])")]
    [InlineData(TargetSqlDialect.SqlServer, "trim(TRAILING FROM name)", "RTRIM([name])")]
    [InlineData(TargetSqlDialect.SqlServer, "trim(BOTH 'x' FROM name)", "TRIM(N'x' FROM [name])")]
    [InlineData(TargetSqlDialect.Sqlite, "trim(name)", "TRIM(\"name\")")]
    [InlineData(TargetSqlDialect.Sqlite, "trim(LEADING 'x' FROM name)", "LTRIM(\"name\", 'x')")]
    [InlineData(TargetSqlDialect.Sqlite, "trim(name, 'x')", "TRIM(\"name\", 'x')")]
    public void Trim(TargetSqlDialect dialect, string call, string expected)
    {
        Assert.Contains(expected, Generate($"SELECT {call} FROM orders", dialect));
    }

    [Fact]
    public void Trim_LeadingChars_SqlServer_IsRejected()
    {
        // LTRIM(x, chars) exists only from SQL Server 2022.
        Assert.Throws<AstBuildException>(() => Generate("SELECT trim(LEADING 'x' FROM name) FROM orders", TargetSqlDialect.SqlServer));
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "POSITION('a' IN \"name\")")]
    [InlineData(TargetSqlDialect.SqlServer, "CHARINDEX(N'a', [name])")]
    [InlineData(TargetSqlDialect.Sqlite, "INSTR(\"name\", 'a')")]
    [InlineData(TargetSqlDialect.Oracle, "INSTR(\"NAME\", 'a')")]
    public void Position(TargetSqlDialect dialect, string expected)
    {
        Assert.Contains(expected, Generate("SELECT position('a' IN name) FROM orders", dialect));
    }

    [Fact]
    public void SpecialForms_FollowTheFunctionPolicy()
    {
        var options = new RlsOptions
        {
            EnforceFunctionPolicy = true,
            AllowedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "upper" }
        };

        Assert.Throws<SecurityException>(() => Generate("SELECT trim(name) FROM orders", TargetSqlDialect.PostgreSql, options));
    }

    [Fact]
    public void SubqueryInsideSpecialForm_GetsRowFilter()
    {
        string sql = Generate("SELECT substring(name FROM (SELECT MIN(id) FROM customers)) FROM orders", TargetSqlDialect.PostgreSql);

        Assert.Contains("FROM \"customers\" WHERE \"tenant_id\" = 't1'", sql);
    }
}
