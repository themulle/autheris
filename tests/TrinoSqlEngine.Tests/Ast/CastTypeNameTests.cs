namespace TrinoSqlEngine.Tests.Ast;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

/// <summary>
/// Wunsch 4, Phase 6c: CAST target types were emitted with their Trino names. SQL Server has no <c>double</c> or
/// <c>boolean</c>, and its <c>timestamp</c> is rowversion; PostgreSQL has no <c>double</c>/<c>tinyint</c>; Oracle needs
/// VARCHAR2/NUMBER. TRY_CAST exists only in SQL Server, DuckDB and Snowflake.
/// </summary>
public sealed class CastTypeNameTests
{
    private readonly FastSqlEngine _engine = new();

    private string Cast(string type, TargetSqlDialect dialect, string function = "CAST") =>
        _engine.GenerateGovernedSql($"SELECT {function}(amount AS {type}) FROM orders", new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 't1'"),
            TargetDialect = dialect
        });

    [Theory]
    [InlineData("double", "AS float)")]
    [InlineData("boolean", "AS bit)")]
    [InlineData("timestamp", "AS datetime2)")]
    [InlineData("timestamp(3)", "AS datetime2(3))")]
    [InlineData("timestamp with time zone", "AS datetimeoffset)")]
    [InlineData("varchar", "AS nvarchar(max))")]
    [InlineData("varchar(20)", "AS nvarchar(20))")]
    [InlineData("char(2)", "AS nchar(2))")]
    [InlineData("integer", "AS int)")]
    [InlineData("decimal(10, 2)", "AS decimal(10,2))")]
    [InlineData("date", "AS date)")]
    public void SqlServer(string type, string expected)
    {
        Assert.Contains(expected, Cast(type, TargetSqlDialect.SqlServer));
    }

    [Theory]
    [InlineData("double", "AS double precision)")]
    [InlineData("tinyint", "AS smallint)")]
    [InlineData("varbinary", "AS bytea)")]
    [InlineData("varchar", "AS varchar)")]
    [InlineData("timestamp with time zone", "AS timestamp with time zone)")]
    public void PostgreSql(string type, string expected)
    {
        Assert.Contains(expected, Cast(type, TargetSqlDialect.PostgreSql));
    }

    [Theory]
    [InlineData("double", "AS BINARY_DOUBLE)")]
    [InlineData("varchar", "AS VARCHAR2(4000))")]
    [InlineData("varchar(20)", "AS VARCHAR2(20))")]
    [InlineData("bigint", "AS NUMBER(19))")]
    [InlineData("integer", "AS NUMBER(10))")]
    public void Oracle(string type, string expected)
    {
        Assert.Contains(expected, Cast(type, TargetSqlDialect.Oracle));
    }

    [Theory]
    [InlineData("boolean", TargetSqlDialect.Oracle)]
    [InlineData("time", TargetSqlDialect.Oracle)]
    [InlineData("uuid", TargetSqlDialect.Oracle)]
    public void Unmappable_IsRejected(string type, TargetSqlDialect dialect)
    {
        Assert.Throws<AstBuildException>(() => Cast(type, dialect));
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.DuckDb)]
    [InlineData(TargetSqlDialect.Snowflake)]
    public void TryCast_WhereSupported(TargetSqlDialect dialect)
    {
        Assert.Contains("TRY_CAST(", Cast("integer", dialect, "TRY_CAST"));
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.Oracle)]
    public void TryCast_WithoutSupport_IsRejected(TargetSqlDialect dialect)
    {
        Assert.Throws<AstBuildException>(() => Cast("integer", dialect, "TRY_CAST"));
    }
}
