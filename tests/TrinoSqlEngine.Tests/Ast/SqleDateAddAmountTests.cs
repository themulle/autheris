namespace TrinoSqlEngine.Tests.Ast;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using Xunit;

/// <summary>SQLE-06: a date_add amount whose year/week multiplication would wrap is rejected, never evaluated as a small amount.</summary>
[Trait("Category", "SqlGate")]
public sealed class SqleDateAddAmountTests
{
    private readonly FastSqlEngine _engine = new();

    private void Build(string sql)
    {
        var (tree, _) = _engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        new SqlAstBuilder(new AstBuilderOptions { TranslateTrinoDateFunctions = true }).BuildStatement(tree);
    }

    [Theory]
    [InlineData("SELECT date_add('year', 4611686018427387905, ts) FROM t")]
    [InlineData("SELECT date_add('week', 7905747460161236407, ts) FROM t")]
    [InlineData("SELECT date_add('day', 9223372036854775807, ts) FROM t")]
    [InlineData("SELECT date_add('day', -9223372036854775807, ts) FROM t")]
    public void DateAdd_AmountOutOfRange_IsRejected(string sql) =>
        Assert.Throws<AstBuildException>(() => Build(sql));

    [Fact]
    public void DateAdd_ReasonableAmount_IsAccepted() => Build("SELECT date_add('year', 100, ts), date_add('week', -52, ts) FROM t");
}
