namespace TrinoSqlEngine.Tests.Ast;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Nodes;
using Xunit;

/// <summary>#321 (SEC-RG-03): a set operation whose operands project a different column count is rejected before the provider.</summary>
[Trait("Category", "SqlGate")]
public sealed class SqlAstBuilderSetOperationTests
{
    private readonly FastSqlEngine _engine = new();

    private SqlStatement Build(string sql)
    {
        var (tree, _) = _engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        return new SqlAstBuilder().BuildStatement(tree);
    }

    [Theory]
    [InlineData("SELECT a, b FROM t UNION SELECT a FROM u")]
    [InlineData("SELECT a FROM t UNION ALL SELECT a, b FROM u")]
    [InlineData("SELECT a, b FROM t INTERSECT SELECT a FROM u")]
    [InlineData("SELECT a, b FROM t EXCEPT SELECT a FROM u")]
    [InlineData("SELECT a FROM t UNION SELECT a FROM u UNION SELECT a, b FROM v")]
    [InlineData("SELECT a, b FROM t UNION SELECT a FROM u UNION SELECT a FROM v")]
    public void UnionDegreeMismatch_Throws(string sql) =>
        Assert.Throws<SetOperationDegreeMismatchException>(() => Build(sql));

    [Theory]
    [InlineData("SELECT a, b FROM t UNION SELECT a, b FROM u")]
    [InlineData("SELECT a FROM t INTERSECT SELECT a FROM u UNION SELECT 1")]
    public void EqualDegree_Builds(string sql) => Assert.IsType<SelectStatement>(Build(sql));

    [Theory]
    [InlineData("SELECT * FROM t UNION SELECT a FROM u")]
    [InlineData("SELECT a, b FROM t UNION SELECT * FROM u")]
    public void Wildcard_IsNotChecked(string sql) => Assert.IsType<SelectStatement>(Build(sql));

    [Fact]
    public void Message_IsFixed_NoColumnText()
    {
        var ex = Assert.Throws<SetOperationDegreeMismatchException>(() => Build("SELECT secret_col, b FROM t UNION SELECT a FROM u"));
        Assert.DoesNotContain("secret_col", ex.Message, StringComparison.Ordinal);
    }
}
