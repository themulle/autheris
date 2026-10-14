namespace TrinoSqlEngine.Tests.Ast.Visitors;

using System;
using System.Collections.Generic;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;

[Trait("Category", "SqlGate")]
public sealed class AstSimplificationVisitorTests
{
    private readonly FastSqlEngine _engine = new();

    private SqlStatement SimplifySql(string sql)
    {
        var (tree, _) = _engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        var builder = new SqlAstBuilder(new AstBuilderOptions { EnforceReadOnlyQueries = false });
        var statement = builder.BuildStatement(tree);
        var simplifier = new AstSimplificationVisitor();
        return (SqlStatement)simplifier.Visit(statement);
    }

    private string GenerateSql(SqlStatement statement, TargetSqlDialect dialect = TargetSqlDialect.PostgreSql)
    {
        var generator = SqlDialectGeneratorFactory.GetGenerator(dialect);
        return generator.GenerateSql(statement);
    }

    [Fact]
    public void And_WithTrue_RemovesTrueLiteral()
    {
        var stmt = SimplifySql("SELECT id FROM orders WHERE id = 1 AND 1 = 1");
        var sql = GenerateSql(stmt);

        // "1 = 1" should be folded to TRUE and eliminated from AND
        Assert.DoesNotContain("1 = 1", sql);
        Assert.Contains("\"id\" = 1", sql);
    }

    [Fact]
    public void And_WithFalse_CollapsesToFalse()
    {
        var stmt = SimplifySql("SELECT id FROM orders WHERE id = 1 AND 1 = 2");
        var sql = GenerateSql(stmt);

        // "1 = 2" folds to FALSE, entire WHERE collapses to 1 = 0
        Assert.Contains("1 = 0", sql);
        Assert.DoesNotContain("\"id\" = 1", sql);
    }

    [Fact]
    public void Or_WithFalse_RemovesFalseLiteral()
    {
        var stmt = SimplifySql("SELECT id FROM orders WHERE id = 1 OR 1 = 2");
        var sql = GenerateSql(stmt);

        // "1 = 2" folds to FALSE and is pruned from OR
        Assert.DoesNotContain("1 = 2", sql);
        Assert.Contains("\"id\" = 1", sql);
    }

    [Fact]
    public void Or_WithTrue_CollapsesToTrue()
    {
        var stmt = SimplifySql("SELECT id FROM orders WHERE id = 1 OR 1 = 1");
        var sql = GenerateSql(stmt);

        // "1 = 1" folds to TRUE, entire OR becomes TRUE (1 = 1)
        Assert.Contains("1 = 1", sql);
    }

    [Fact]
    public void Not_DoubleNegation_Eliminates()
    {
        var stmt = SimplifySql("SELECT id FROM orders WHERE NOT (NOT (id = 1))");
        var sql = GenerateSql(stmt);

        Assert.DoesNotContain("NOT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"id\" = 1", sql);
    }

    [Fact]
    public void Not_ComparisonInversion_InvertsCorrectly()
    {
        var stmt = SimplifySql("SELECT id FROM orders WHERE NOT (id = 1)");
        var sql = GenerateSql(stmt);

        Assert.Contains("\"id\" <> 1", sql);
        Assert.DoesNotContain("NOT (", sql);
    }

    [Fact]
    public void LiteralEquality_SameStringConstants_FoldsToTrue()
    {
        var stmt = SimplifySql("SELECT id FROM orders WHERE 'tenant_a' = 'tenant_a' AND id = 5");
        var sql = GenerateSql(stmt);

        Assert.DoesNotContain("'tenant_a'", sql);
        Assert.Contains("\"id\" = 5", sql);
    }

    [Fact]
    public void LiteralEquality_DifferentStringConstants_FoldsToFalse()
    {
        var stmt = SimplifySql("SELECT id FROM orders WHERE 'tenant_a' = 'tenant_b' AND id = 5");
        var sql = GenerateSql(stmt);

        Assert.Contains("1 = 0", sql);
    }

    [Fact]
    public void NullChecks_ConstantLiterals_FoldCorrectly()
    {
        var stmt = SimplifySql("SELECT id FROM orders WHERE 'val' IS NOT NULL AND id = 10");
        var sql = GenerateSql(stmt);

        Assert.DoesNotContain("IS NOT NULL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"id\" = 10", sql);
    }

    [Fact]
    public void ThreeValuedLogic_NullEquality_DoesNotFoldToTrue()
    {
        // In SQL NULL = NULL is unknown / false, NEVER true!
        var stmt = SimplifySql("SELECT id FROM orders WHERE NULL = NULL");
        var sql = GenerateSql(stmt);

        // Must not become 1 = 1
        Assert.DoesNotContain("1 = 1", sql);
    }

    [Fact]
    public void PredicateDeduplication_IdenticalOperands_Deduplicates()
    {
        // Simulates query with duplicated tenant check: (tenant_id = 't1') AND (tenant_id = 't1')
        var stmt = SimplifySql("SELECT id FROM orders WHERE tenant_id = 't1' AND tenant_id = 't1'");
        var sql = GenerateSql(stmt);

        // Must occur exactly once
        int firstIdx = sql.IndexOf("\"tenant_id\" = 't1'", StringComparison.Ordinal);
        int lastIdx = sql.LastIndexOf("\"tenant_id\" = 't1'", StringComparison.Ordinal);

        Assert.True(firstIdx >= 0);
        Assert.Equal(firstIdx, lastIdx);
    }

    [Fact]
    public void ContradictionDetection_PreservesNullSemantics_DoesNotCollapseToFalse()
    {
        // Audit finding: col='a' AND col='b' must NOT fold to 1=0 because in SQL 3-valued logic NULL='a' AND NULL='b' is UNKNOWN, not FALSE.
        var stmt = SimplifySql("SELECT id FROM orders WHERE tenant_id = 't1' AND tenant_id = 't2'");
        var sql = GenerateSql(stmt);

        Assert.DoesNotContain("1 = 0", sql);
        Assert.Contains("\"tenant_id\" = 't1'", sql);
        Assert.Contains("\"tenant_id\" = 't2'", sql);
    }

    [Fact]
    public void Parameters_AreNeverFoldedAtCompileTime()
    {
        // When parameters are present, they must be preserved as runtime operands
        var stmt = SimplifySql("SELECT id FROM orders WHERE id = 1 AND tenant_id = 't1'");
        var sql = GenerateSql(stmt);

        Assert.Contains("\"id\" = 1", sql);
        Assert.Contains("\"tenant_id\" = 't1'", sql);
    }

    // The compile path simplifies the USER tree (before any injection) and binds the literals that remain.
    [Theory]
    [InlineData("SELECT id FROM orders WHERE 1 = 1 AND status = 'x' AND status = 'x'")]
    [InlineData("DELETE FROM orders WHERE 1 = 1 AND status = 'x' AND status = 'x'")]
    public void EndToEnd_Compile_IncorporatesSimplifier(string sql)
    {
        var env = new TrinoSqlEngine.Tests.Security.ProbeEnv(TargetSqlDialect.PostgreSql);
        var compiled = env.Compile(sql);

        // 1 = 1 is gone and the duplicate conjunct is deduplicated: one status comparison, one bound query literal
        Assert.DoesNotContain("1 = 1", compiled.Sql);
        Assert.Single(compiled.Parameters, p => p.Origin == TrinoSqlEngine.Ast.Emit.ParameterOrigin.QueryLiteral);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(compiled.Sql, "\"status\" = "));
    }

    // ---- literal comparison folding must be exact, three-valued and dialect-independent ----

    private Expression SimplifiedWhere(string sql) =>
        ((QuerySpecification)((SelectStatement)SimplifySql(sql)).Body).Where!;

    private static bool IsCanonical(Expression e, long right) =>
        e is BinaryExpression { Operator: BinaryOperator.Equal, Left: LiteralExpression { Value: 1L }, Right: LiteralExpression { Value: var r } } && Equals(r, right);

    [Theory]
    [InlineData("1 = 1.0", true)]
    [InlineData("1.0 = 1", true)]
    [InlineData("1 <> 1.0", false)]
    [InlineData("1 < 1.5", true)]
    [InlineData("1 >= 1.5", false)]
    [InlineData("2 = 2.00", true)]
    [InlineData("1.0 = 1.00", true)]
    [InlineData("1.50 <> 1.5", false)]
    [InlineData("1e0 = 1", true)]
    [InlineData("1 = 2", false)]
    [InlineData("9007199254740993 = 9007199254740993", true)]
    [InlineData("9007199254740993 = 9007199254740992", false)]
    [InlineData("'a' = 'a'", true)]
    [InlineData("'a' <> 'a'", false)]
    [InlineData("'tenant_a' = 'tenant_b'", false)]
    [InlineData("'tenant_a' <> 'tenant_b'", true)]
    public void LiteralComparison_FoldsOnlyWhenCertain(string predicate, bool expected)
    {
        var where = SimplifiedWhere($"SELECT id FROM orders WHERE {predicate}");
        Assert.True(IsCanonical(where, expected ? 1L : 0L), $"'{predicate}' must fold to {expected} but became {where}");
    }

    [Theory]
    [InlineData("'1' = 1")]
    [InlineData("1 = '1'")]
    [InlineData("'1.0' = 1")]
    [InlineData("NULL = NULL")]
    [InlineData("1 = NULL")]
    [InlineData("NULL <> 1.0")]
    [InlineData("'a' = 'A'")]
    [InlineData("'a' = 'a '")]
    [InlineData("'a' <> 'A'")]
    [InlineData("'a' <> 'a '")]
    [InlineData("'é' = 'e'")]
    [InlineData("'a' < 'b'")]
    [InlineData("9007199254740993 = 9007199254740992.0")]
    [InlineData("0.1 = 0.1000000000000000055")]
    [InlineData("true = 1")]
    public void LiteralComparison_NotFoldedWhenDialectDependentOrUnknown(string predicate)
    {
        var where = SimplifiedWhere($"SELECT id FROM orders WHERE {predicate}");
        Assert.True(where is BinaryExpression { Left: LiteralExpression, Right: LiteralExpression } b && b.Operator != BinaryOperator.And,
            $"'{predicate}' must stay a literal comparison but became {where}");
        Assert.False(IsCanonical(where, 1L) || IsCanonical(where, 0L), $"'{predicate}' must not fold to a constant");
    }

    [Theory]
    [InlineData("NOT (1 = 1.0)", false)]
    [InlineData("NOT (1 <> 1.0)", true)]
    [InlineData("NOT (2 = 2.00)", false)]
    public void Not_OfNumericLiteralComparison_IsExact(string predicate, bool expected)
    {
        var where = SimplifiedWhere($"SELECT id FROM orders WHERE {predicate}");
        Assert.True(IsCanonical(where, expected ? 1L : 0L), $"'{predicate}' must fold to {expected} but became {where}");
    }

    [Theory]
    [InlineData("NOT (NULL = NULL)")]
    [InlineData("NOT (1 = NULL)")]
    [InlineData("NOT ('1' = 1)")]
    public void Not_OfUnknownOrMixedComparison_IsNeverConstant(string predicate)
    {
        var where = SimplifiedWhere($"SELECT id FROM orders WHERE {predicate}");
        Assert.False(IsCanonical(where, 1L) || IsCanonical(where, 0L), $"'{predicate}' must not fold to a constant (became {where})");
    }

    [Fact]
    public void Or_WithDecimalTautology_IsAConstantTrueNotDropped()
    {
        // the integer-vs-decimal comparison used to fold to FALSE, silently dropping this OR branch
        Assert.True(IsCanonical(SimplifiedWhere("SELECT id FROM orders WHERE id = 5 OR 1 = 1.0"), 1L));
    }

    [Fact]
    public void Compile_AndNotDecimalTautology_DoesNotWidenDml()
    {
        // "id = 5 AND NOT (1 = 1.0)" is always empty; the wrong fold turned it into "id = 5" (a widened DELETE)
        var env = new TrinoSqlEngine.Tests.Security.ProbeEnv(TargetSqlDialect.PostgreSql);
        Assert.Throws<UnfilteredDmlException>(() => env.Compile("DELETE FROM orders WHERE id = 5 AND NOT (1 = 1.0)"));
        Assert.Throws<UnfilteredDmlException>(() => env.Compile("UPDATE orders SET status = 'x' WHERE id = 5 AND NOT (2 = 2.00)"));
    }

    [Fact]
    public void Compile_OrNotDecimalTautology_KeepsTheRealFilter()
    {
        var env = new TrinoSqlEngine.Tests.Security.ProbeEnv(TargetSqlDialect.PostgreSql);
        var compiled = env.Compile("DELETE FROM orders WHERE id = 5 OR NOT (1 = 1.0)");
        Assert.Contains("\"id\" = ", compiled.Sql);
        Assert.DoesNotContain("1 = 1", compiled.Sql);
    }
}
