namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Linq;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Tests.Compiler;
using Xunit;

/// <summary>SQLE-01, -02, -04, -12: the builder never maps a construct to different semantics.</summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public sealed class SqleBuilderCorrectnessTests
{
    private readonly FastSqlEngine _engine = new();

    private SqlStatement Build(string sql)
    {
        var (tree, _) = _engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        return new SqlAstBuilder(new AstBuilderOptions { EnforceReadOnlyQueries = false }).BuildStatement(tree);
    }

    // ---- SQLE-01 ----

    [Fact]
    public void UnaryPlus_Literal_IsIdentity()
    {
        var compiled = CompilerTestHelpers.Generate(TargetSqlDialect.PostgreSql, "SELECT id FROM t WHERE id = +5");

        Assert.DoesNotContain("NOT", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"id\" = $1", compiled.Sql);
    }

    [Fact]
    public void UnaryPlus_Column_InSelect_Sqlite()
    {
        var compiled = CompilerTestHelpers.Generate(TargetSqlDialect.Sqlite, "SELECT +amount FROM t");

        Assert.DoesNotContain("NOT", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnaryPlus_InDeleteWhere_Sqlite_NoNot()
    {
        var compiled = CompilerTestHelpers.Generate(TargetSqlDialect.Sqlite, Build("DELETE FROM t WHERE id = +5"));

        Assert.DoesNotContain("NOT", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnaryMinus_Unchanged()
    {
        var statement = Assert.IsType<SelectStatement>(Build("SELECT -amount FROM t"));
        var spec = Assert.IsType<QuerySpecification>(statement.Body);
        var item = Assert.IsType<ColumnSelectItem>(Assert.Single(spec.Projections));
        Assert.Equal(UnaryOperator.Negate, Assert.IsType<UnaryExpression>(item.Expression).Operator);
    }

    // ---- SQLE-02 ----

    [Theory]
    [InlineData("SELECT a FROM t UNION CORRESPONDING SELECT a FROM u")]
    [InlineData("SELECT a FROM t EXCEPT CORRESPONDING SELECT a FROM u")]
    [InlineData("SELECT a FROM t INTERSECT CORRESPONDING SELECT a FROM u")]
    public void SetOperation_Corresponding_Rejected(string sql)
    {
        Assert.Throws<AstBuildException>(() => Build(sql));
    }

    // ---- SQLE-04 ----

    [Fact]
    public void NamedTable_WithColumnAliases_Rejected()
    {
        Assert.Throws<AstBuildException>(() => Build("SELECT a FROM t AS x (a, b)"));
    }

    [Fact]
    public void Lateral_ColumnAliases_Emitted()
    {
        var compiled = CompilerTestHelpers.Generate(TargetSqlDialect.PostgreSql,
            "SELECT l.a FROM t, LATERAL (SELECT status, region FROM u WHERE u.id = t.id) AS l (a, b)");

        Assert.Contains("AS \"l\" (\"a\", \"b\")", compiled.Sql);
    }

    // ---- SQLE-12 ----

    [Theory]
    [InlineData("IS TRUE")]
    [InlineData("IS NOT TRUE")]
    [InlineData("IS FALSE")]
    [InlineData("IS NOT FALSE")]
    public void BooleanTest_IsExpandedToCase_WithoutBooleanLiteral(string test)
    {
        foreach (var dialect in new[] { TargetSqlDialect.SqlServer, TargetSqlDialect.Oracle, TargetSqlDialect.Sqlite })
        {
            var compiled = CompilerTestHelpers.Generate(dialect, $"SELECT id FROM t WHERE (flag = 1) {test}");

            Assert.Contains("CASE WHEN", compiled.Sql);
            Assert.DoesNotContain("TRUE", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("FALSE", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("IS TRUE", 1L, 0L)]
    [InlineData("IS NOT TRUE", 0L, 1L)]
    [InlineData("IS FALSE", 1L, 0L)]
    [InlineData("IS NOT FALSE", 0L, 1L)]
    public void BooleanTest_ThenAndElseValues_MatchThreeValuedSemantics(string test, long then, long otherwise)
    {
        var statement = Assert.IsType<DeleteStatement>(Build($"DELETE FROM t WHERE (flag = 1) {test}"));
        var equal = Assert.IsType<BinaryExpression>(statement.Where);
        var caseExpr = Assert.IsType<CaseExpression>(equal.Left);
        var when = Assert.Single(caseExpr.WhenClauses);

        Assert.Equal(then, Assert.IsType<LiteralExpression>(when.Result).Value);
        Assert.Equal(otherwise, Assert.IsType<LiteralExpression>(caseExpr.ElseResult).Value);
        Assert.Equal(test.EndsWith("FALSE", StringComparison.Ordinal), when.Condition is UnaryExpression { Operator: UnaryOperator.Not });
    }

    [Fact]
    public void DeleteWhere_IsNotTrue_NotTriviallyTrue()
    {
        // A DML without a real predicate is refused; the CASE expansion must never be folded to a tautology.
        var statement = Assert.IsType<DeleteStatement>(Build("DELETE FROM t WHERE (flag = 1) IS NOT TRUE"));
        Assert.IsNotType<LiteralExpression>(statement.Where);
        Assert.IsType<CaseExpression>(Assert.IsType<BinaryExpression>(statement.Where).Left);
    }
}
