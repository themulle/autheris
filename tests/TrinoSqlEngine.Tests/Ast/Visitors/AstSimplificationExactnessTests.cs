namespace TrinoSqlEngine.Tests.Ast.Visitors;

using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;

/// <summary>
/// Mutation-gate tests (M-12) pinning the exact rewrite of the simplifier per rule: comparison inversion under NOT, IS NULL folding on
/// literals (never on the empty string, which is NULL on Oracle), constant folding only when certain, operand deduplication with the
/// SQLE-07 identifier case rules, and the canonical TRUE and FALSE predicates. A wrong rewrite would change which rows a security
/// filter lets through, so each rule is asserted on the emitted predicate, not just on a substring.
/// </summary>
[Trait("Category", "SqlGate")]
public sealed class AstSimplificationExactnessTests
{
    private static readonly FastSqlEngine Engine = new();

    private static string Where(string predicate)
    {
        var (tree, _) = Engine.Parse($"SELECT id FROM orders WHERE {predicate}".AsMemory(), SqlTokenSecurityOptions.None);
        var statement = new SqlAstBuilder(new AstBuilderOptions { EnforceReadOnlyQueries = false }).BuildStatement(tree);
        var simplified = (SqlStatement)new AstSimplificationVisitor().Visit(statement);
        string sql = SqlDialectGeneratorFactory.GetGenerator(TargetSqlDialect.PostgreSql).GenerateSql(simplified);
        const string marker = " WHERE ";
        int at = sql.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, sql);
        return sql[(at + marker.Length)..];
    }

    [Theory]
    [InlineData("NOT (id = 1)", "\"id\" <> 1")]
    [InlineData("NOT (id <> 1)", "\"id\" = 1")]
    [InlineData("NOT (id < 1)", "\"id\" >= 1")]
    [InlineData("NOT (id <= 1)", "\"id\" > 1")]
    [InlineData("NOT (id > 1)", "\"id\" <= 1")]
    [InlineData("NOT (id >= 1)", "\"id\" < 1")]
    [InlineData("NOT (id IS NULL)", "\"id\" IS NOT NULL")]
    [InlineData("NOT (id IS NOT NULL)", "\"id\" IS NULL")]
    [InlineData("NOT (NOT (id = 1))", "\"id\" = 1")]
    [InlineData("NOT (NOT (id IS NULL))", "\"id\" IS NULL")]
    [InlineData("NOT (1 = 1)", "1 = 0")]
    [InlineData("NOT (1 = 2)", "1 = 1")]
    [InlineData("NOT (TRUE)", "1 = 0")]
    [InlineData("NOT (FALSE)", "1 = 1")]
    public void Not_IsRewrittenToItsExactNegation(string predicate, string expected) => Assert.Equal(expected, Where(predicate));

    [Theory]
    [InlineData("NOT (id = 1 AND amount = 2)")]
    [InlineData("NOT (id IN (1, 2))")]
    [InlineData("NOT (id LIKE 'a')")]
    [InlineData("NOT id BETWEEN 1 AND 2")]
    public void Not_IsKeptWhenNoRuleApplies(string predicate)
    {
        string sql = Where(predicate);
        Assert.StartsWith("NOT", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("id = 1 AND NULL IS NULL", "\"id\" = 1")]
    [InlineData("id = 1 AND 1 IS NOT NULL", "\"id\" = 1")]
    [InlineData("id = 1 AND 'a' IS NOT NULL", "\"id\" = 1")]
    [InlineData("id = 1 AND 1 IS NULL", "1 = 0")]
    [InlineData("id = 1 AND 'a' IS NULL", "1 = 0")]
    [InlineData("id = 1 AND NULL IS NOT NULL", "1 = 0")]
    [InlineData("id = 1 OR 1 IS NULL", "\"id\" = 1")]
    [InlineData("id = 1 OR NULL IS NULL", "1 = 1")]
    [InlineData("id = 1 OR 1 IS NOT NULL", "1 = 1")]
    [InlineData("id = 1 OR NULL IS NOT NULL", "\"id\" = 1")]
    public void IsNull_OnALiteralFoldsToTheCanonicalPredicate(string predicate, string expected) => Assert.Equal(expected, Where(predicate));

    [Theory]
    [InlineData("'' IS NULL")]
    [InlineData("'' IS NOT NULL")]
    [InlineData("'' = ''")]
    [InlineData("'' <> 'a'")]
    [InlineData("'a' = ''")]
    [InlineData("NULL = NULL")]
    [InlineData("1 = NULL")]
    [InlineData("NULL <> 1")]
    [InlineData("NULL < 1")]
    [InlineData("1 >= NULL")]
    public void NullAndEmptyString_AreNeverFolded(string predicate)
    {
        string sql = Where(predicate);
        Assert.NotEqual("1 = 1", sql);
        Assert.NotEqual("1 = 0", sql);
    }

    [Theory]
    [InlineData("1 = 1", "1 = 1")]
    [InlineData("2 = 2", "1 = 1")]
    [InlineData("1 = 2", "1 = 0")]
    [InlineData("2 <> 1", "1 = 1")]
    [InlineData("1 <> 1", "1 = 0")]
    [InlineData("1 < 2", "1 = 1")]
    [InlineData("2 < 1", "1 = 0")]
    [InlineData("2 <= 2", "1 = 1")]
    [InlineData("3 <= 2", "1 = 0")]
    [InlineData("3 > 2", "1 = 1")]
    [InlineData("2 > 3", "1 = 0")]
    [InlineData("2 >= 2", "1 = 1")]
    [InlineData("1 >= 2", "1 = 0")]
    [InlineData("1 = 1.0", "1 = 1")]
    [InlineData("'a' = 'a'", "1 = 1")]
    [InlineData("TRUE", "1 = 1")]
    [InlineData("FALSE", "1 = 0")]
    [InlineData("(1 = 1)", "1 = 1")]
    [InlineData("((1 = 2))", "1 = 0")]
    [InlineData("(TRUE)", "1 = 1")]
    [InlineData("(FALSE)", "1 = 0")]
    public void ConstantWhere_BecomesTheCanonicalTrueOrFalse(string predicate, string expected) => Assert.Equal(expected, Where(predicate));

    [Theory]
    [InlineData("id = 1 AND 1 = 1", "\"id\" = 1")]
    [InlineData("1 = 1 AND id = 1", "\"id\" = 1")]
    [InlineData("id = 1 AND TRUE", "\"id\" = 1")]
    [InlineData("id = 1 AND 1 = 2", "1 = 0")]
    [InlineData("1 = 2 AND id = 1", "1 = 0")]
    [InlineData("id = 1 AND FALSE", "1 = 0")]
    [InlineData("id = 1 AND (1 = 2)", "1 = 0")]
    [InlineData("id = 1 AND (1 = 1)", "\"id\" = 1")]
    [InlineData("1 = 1 AND 1 = 1", "1 = 1")]
    [InlineData("id = 1 OR 1 = 2", "\"id\" = 1")]
    [InlineData("1 = 2 OR id = 1", "\"id\" = 1")]
    [InlineData("id = 1 OR FALSE", "\"id\" = 1")]
    [InlineData("id = 1 OR 1 = 1", "1 = 1")]
    [InlineData("1 = 1 OR id = 1", "1 = 1")]
    [InlineData("id = 1 OR TRUE", "1 = 1")]
    [InlineData("id = 1 OR (1 = 1)", "1 = 1")]
    [InlineData("id = 1 OR (1 = 2)", "\"id\" = 1")]
    [InlineData("1 = 2 OR 1 = 2", "1 = 0")]
    [InlineData("1 = 2 OR FALSE", "1 = 0")]
    [InlineData("id = 1 AND amount = 2 AND 1 = 1", "\"id\" = 1 AND \"amount\" = 2")]
    [InlineData("id = 1 AND 1 = 1 AND amount = 2", "\"id\" = 1 AND \"amount\" = 2")]
    [InlineData("id = 1 OR amount = 2 OR 1 = 2", "\"id\" = 1 OR \"amount\" = 2")]
    [InlineData("id = 1 OR 1 = 2 OR amount = 2", "\"id\" = 1 OR \"amount\" = 2")]
    [InlineData("id = 1 AND amount = 2 AND 1 = 2", "1 = 0")]
    [InlineData("id = 1 OR amount = 2 OR 1 = 1", "1 = 1")]
    public void BooleanConstants_ArePrunedOrShortCircuited(string predicate, string expected) => Assert.Equal(expected, Where(predicate));

    [Theory]
    [InlineData("id = 1 AND id = 1", "\"id\" = 1")]
    [InlineData("id = 1 OR id = 1", "\"id\" = 1")]
    [InlineData("id = 1 AND amount = 2 AND id = 1", "\"id\" = 1 AND \"amount\" = 2")]
    [InlineData("id = 1 OR amount = 2 OR id = 1", "\"id\" = 1 OR \"amount\" = 2")]
    [InlineData("amount = 2 AND id = 1 AND amount = 2 AND id = 1", "\"amount\" = 2 AND \"id\" = 1")]
    [InlineData("id = 1 AND ID = 1", "\"id\" = 1")]
    [InlineData("o.id = 1 AND O.ID = 1", "\"o\".\"id\" = 1")]
    [InlineData("(id = 1) AND (id = 1)", "\"id\" = 1")]
    [InlineData("id BETWEEN 1 AND 2 AND id BETWEEN 1 AND 2", "\"id\" BETWEEN 1 AND 2")]
    [InlineData("id IS NULL AND id IS NULL", "\"id\" IS NULL")]
    [InlineData("id IS NULL OR id IS NULL", "\"id\" IS NULL")]
    [InlineData("(id = 1 AND amount = 2) AND id = 1", "\"id\" = 1 AND \"amount\" = 2")]
    [InlineData("id = 1 AND (amount = 2 AND id = 1)", "\"id\" = 1 AND \"amount\" = 2")]
    public void IdenticalOperands_AreDeduplicatedInOrder(string predicate, string expected) => Assert.Equal(expected, Where(predicate));

    [Theory]
    [InlineData("id = 1 AND id = 2")]
    [InlineData("id = 1 AND id = '1'")]
    [InlineData("id = 1 AND id <> 1")]
    [InlineData("id = 1 AND amount = 1")]
    [InlineData("id = 1 AND o.id = 1")]
    [InlineData("o.id = 1 AND p.id = 1")]
    [InlineData("\"id\" = 1 AND \"ID\" = 1")]
    [InlineData("id = 1 AND \"ID\" = 1")]
    [InlineData("id BETWEEN 1 AND 2 AND id NOT BETWEEN 1 AND 2")]
    [InlineData("id BETWEEN 1 AND 2 AND id BETWEEN 1 AND 3")]
    [InlineData("id BETWEEN 1 AND 2 AND id BETWEEN 0 AND 2")]
    [InlineData("id BETWEEN 1 AND 2 AND amount BETWEEN 1 AND 2")]
    [InlineData("id IS NULL AND id IS NOT NULL")]
    [InlineData("id IS NULL AND amount IS NULL")]
    [InlineData("id = 1 AND 1 = id")]
    [InlineData("id = 'a' AND id = 'A'")]
    [InlineData("id = 1 AND id = 1.5")]
    [InlineData("id = 1 OR id = 2")]
    public void DifferentOperands_AreKeptApart(string predicate)
    {
        string sql = Where(predicate);
        string op = predicate.Contains(" OR ", StringComparison.Ordinal) ? " OR " : " AND ";
        Assert.Contains(op, sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-id = 1", "-(\"id\") = 1")]
    [InlineData("id = 1 AND -id = 2", "\"id\" = 1 AND -(\"id\") = 2")]
    [InlineData("id + 1 = 2", "\"id\" + 1 = 2")]
    [InlineData("id = 1", "\"id\" = 1")]
    public void NonBooleanShapes_AreLeftAlone(string predicate, string expected) => Assert.Equal(expected, Where(predicate));

    [Fact]
    public void Simplification_AppliesInsideSubqueriesAndKeepsTheSelectIntact()
    {
        string sql = Where("id IN (SELECT id FROM orders WHERE amount = 1 AND 1 = 1 AND amount = 1)");
        Assert.Contains("\"amount\" = 1", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("1 = 1", sql, StringComparison.Ordinal);
        Assert.Equal(sql.IndexOf("\"amount\" = 1", StringComparison.Ordinal), sql.LastIndexOf("\"amount\" = 1", StringComparison.Ordinal));
    }

    [Fact]
    public void QueryWithoutWhere_IsReturnedUnchanged()
    {
        var (tree, _) = Engine.Parse("SELECT id FROM orders".AsMemory(), SqlTokenSecurityOptions.None);
        var statement = new SqlAstBuilder(new AstBuilderOptions()).BuildStatement(tree);
        Assert.Same(statement, new AstSimplificationVisitor().Visit(statement));
    }
}
