using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>SQLE-05: right-nested equal-precedence operands and comparisons of comparisons are parenthesized on every dialect.</summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public class SqleExpressionPrecedenceTests
{
    public static TheoryData<TargetSqlDialect> Dialects() => new()
    {
        TargetSqlDialect.SqlServer, TargetSqlDialect.DuckDb, TargetSqlDialect.PostgreSql,
        TargetSqlDialect.Oracle, TargetSqlDialect.Databricks, TargetSqlDialect.Sqlite
    };

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Multiply_RightModulo_Parenthesized(TargetSqlDialect dialect)
    {
        var sql = CompilerTestHelpers.Generate(dialect, "SELECT a * (b % c) FROM t").Sql;

        Assert.Contains("* (", sql);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Multiply_RightDivide_Parenthesized(TargetSqlDialect dialect)
    {
        var sql = CompilerTestHelpers.Generate(dialect, "SELECT a * (b / c) FROM t").Sql;

        Assert.Contains("* (", sql);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Comparison_OfComparison_Parenthesized(TargetSqlDialect dialect)
    {
        var sql = CompilerTestHelpers.Generate(dialect, "SELECT id FROM t WHERE 1 = (region = 'x')").Sql;

        Assert.Contains("= (", sql);
    }

    [Fact]
    public void SqlServer_ConcatOfAdd_Parenthesized()
    {
        var sql = CompilerTestHelpers.Generate(TargetSqlDialect.SqlServer, "SELECT 'a' || (b + c) FROM t").Sql;

        Assert.Contains("+ (", sql);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.SqlServer)]
    public void AndChain_NotParenthesized(TargetSqlDialect dialect)
    {
        var sql = CompilerTestHelpers.Generate(dialect, "SELECT id FROM t WHERE a = 1 AND (b = 2 AND c = 3) AND d = 4").Sql;

        Assert.DoesNotContain("(", sql);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void LeftAssociativeSubtract_Unchanged(TargetSqlDialect dialect)
    {
        var sql = CompilerTestHelpers.Generate(dialect, "SELECT a - b - c FROM t").Sql;

        Assert.DoesNotContain("(", sql);
    }
}
