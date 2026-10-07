namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Generators;
using Xunit;

/// <summary>
/// SQL-3: the AST generators emit cast target types and EXTRACT fields verbatim; only plain tokens are allowed, and an
/// unknown dialect no longer falls back to ANSI.
/// </summary>
public sealed class SafeTokenEmissionSql3Tests
{
    private readonly FastSqlEngine _engine = new();

    private static RlsOptions Options(TargetSqlDialect dialect) => new()
    {
        PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42"),
        TargetDialect = dialect
    };

    [Theory]
    [InlineData("SELECT CAST(amount AS decimal(10, 2)) FROM orders", "CAST(\"amount\" AS decimal(10, 2))")]
    [InlineData("SELECT CAST(ts AS timestamp(3) with time zone) FROM orders", "AS timestamp(3) with time zone)")]
    [InlineData("SELECT CAST(x AS double precision) FROM orders", "AS double precision)")]
    [InlineData("SELECT EXTRACT(year FROM ts) FROM orders", "EXTRACT(YEAR FROM")]
    public void PlainTypesAndFields_AreEmittedNormalized(string sql, string expected)
    {
        var result = _engine.GenerateGovernedSql(sql.AsMemory(), Options(TargetSqlDialect.PostgreSql));

        Assert.Contains(expected, result, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT CAST(id AS \"int) FROM secrets --\") FROM orders")]
    [InlineData("SELECT CAST(id AS \"varchar\") FROM orders")]
    [InlineData("SELECT EXTRACT(\"year) FROM secrets --\" FROM ts) FROM orders")]
    [InlineData("SELECT EXTRACT(foo FROM ts) FROM orders")]
    public void QuotedOrUnknownTokens_AreRejected(string sql)
    {
        Assert.ThrowsAny<Exception>(() => _engine.GenerateGovernedSql(sql.AsMemory(), Options(TargetSqlDialect.SqlServer)));
    }

    [Fact]
    public void UnknownDialect_HasNoAnsiFallback()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlDialectGeneratorFactory.GetGenerator((TargetSqlDialect)999));
    }

    [Theory]
    [InlineData("int) FROM secrets --")]
    [InlineData("\"x\"")]
    [InlineData("varchar; DROP TABLE t")]
    public void EnsureTypeName_RejectsSymbols(string typeName)
    {
        Assert.Throws<SecurityException>(() => TrinoSqlEngine.Ast.SqlSafeTokens.EnsureTypeName(typeName));
    }

    [Theory]
    [InlineData("X'0A1B'", true)]
    [InlineData("X''", true)]
    [InlineData("X'zz'", false)]
    [InlineData("X'0' OR 1=1 --'", false)]
    public void EnsureBinaryLiteral_AcceptsHexOnly(string literal, bool valid)
    {
        if (valid)
        {
            Assert.Equal(literal, TrinoSqlEngine.Ast.SqlSafeTokens.EnsureBinaryLiteral(literal));
        }
        else
        {
            Assert.Throws<SecurityException>(() => TrinoSqlEngine.Ast.SqlSafeTokens.EnsureBinaryLiteral(literal));
        }
    }
}
