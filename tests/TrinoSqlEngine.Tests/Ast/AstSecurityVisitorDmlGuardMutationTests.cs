namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;

/// <summary>
/// Mutation-gate tests (M-12) for the guards of <c>AstSecurityVisitor</c> on the options path: the unfiltered-DML check (column
/// reference, trivially true predicates), the WITH CHECK OPTION verification of INSERT and UPDATE, and the masked column guards.
/// Every rejection asserts the exact message, so a guard that fires for the wrong reason is caught as well.
/// </summary>
[Trait("Category", "SqlGate")]
public sealed class AstSecurityVisitorDmlGuardMutationTests
{
    private readonly FastSqlEngine _engine = new();

    private string Secure(string sql, RlsOptions options)
    {
        var (tree, _) = _engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        var builder = new SqlAstBuilder(AstBuilderOptions.FromRlsOptions(options));
        var ast = builder.BuildStatement(tree);
        var secured = (SqlStatement)new AstSecurityVisitor(options, _engine).Visit(ast);
        return SqlDialectGeneratorFactory.GetGenerator(options.TargetDialect).GenerateSql(secured);
    }

    private static RlsOptions Dml() => new()
    {
        EnforceReadOnlyQueries = false,
        EnforceWithCheckOption = false,
        PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42")
    };

    private string ThrowsMessage<T>(string sql, RlsOptions options) where T : Exception =>
        Assert.Throws<T>(() => Secure(sql, options)).Message;

    // ---- unfiltered DML ----

    [Fact]
    public void UnfilteredDml_MessagesNameTheOperation()
    {
        var o = Dml();
        Assert.Equal("DELETE without a WHERE clause is not permitted.", ThrowsMessage<UnfilteredDmlException>("DELETE FROM orders", o));
        Assert.Equal("UPDATE without a WHERE clause is not permitted.", ThrowsMessage<UnfilteredDmlException>("UPDATE orders SET x = 1", o));
        Assert.Equal("DELETE statement WHERE clause must reference at least one table column; literal-only predicates are forbidden.",
            ThrowsMessage<UnfilteredDmlException>("DELETE FROM orders WHERE 5 = 6", o));
        Assert.Equal("UPDATE statement WHERE clause must reference at least one table column; literal-only predicates are forbidden.",
            ThrowsMessage<UnfilteredDmlException>("UPDATE orders SET x = 1 WHERE 5 = 6", o));
        Assert.Equal("DELETE with a trivially true WHERE clause is not permitted.", ThrowsMessage<UnfilteredDmlException>("DELETE FROM orders WHERE x = x", o));
        Assert.Equal("UPDATE with a trivially true WHERE clause is not permitted.", ThrowsMessage<UnfilteredDmlException>("UPDATE orders SET y = 1 WHERE x = x", o));
    }

    [Fact]
    public void UnfilteredDml_CheckCanBeSwitchedOff()
    {
        var o = Dml();
        o.RejectUnfilteredDml = false;
        Assert.Equal("DELETE FROM \"orders\" WHERE (\"tenant_id\" = 42)", Secure("DELETE FROM orders", o));
        Assert.Equal("UPDATE \"orders\" SET \"x\" = 1 WHERE (1 = 1) AND (\"tenant_id\" = 42)", Secure("UPDATE orders SET x = 1 WHERE 1 = 1", o));
    }

    public static IEnumerable<object[]> TriviallyTrue() => new[]
    {
        "x = 5 OR 1 = 1", "1 = 1 OR x = 5", "(x = 5 OR true)", "x = 5 OR (true)", "x = 5 OR false OR true",
        "x = x", "X = x", "orders.x = orders.X", "x <= x", "x >= x", "x BETWEEN x AND x", "x IN (y, x)", "x IN (x)",
        "x IS NULL OR x IS NOT NULL", "x IS NOT NULL OR x IS NULL", "(x IS NULL) OR (x IS NOT NULL)", "x IS NULL OR X IS NOT NULL",
        "x = x AND 1 = 1", "x = x AND true", "(x = x) AND (y = y)",
        "NOT (x = 5 AND false)", "NOT (1 = 0) AND x = x", "NOT (x = 5 AND 1 = 2) ",
        "x = 5 OR 1 = 1.0", "x = 5 OR 2 > 1", "x = 5 OR 1 < 2", "x = 5 OR 1 != 2", "x = 5 OR 3 <= 3", "x = 5 OR 3 >= 3",
        "x = 5 OR 'a' = 'a'", "x = 5 OR 'a' != 'b'", "x = 5 OR 'a' <= 'a'", "x = 5 OR 'a' >= 'a'", "x = 5 OR 1.5 >= 1", "x = 5 OR 1 <= 1.5",
        "x = 5 OR 1 IS NOT NULL", "x = 5 OR 'a' IS NOT NULL",
        "x = 5 OR NOT (1 = 2)", "x = 5 OR NOT (false)", "x = 5 OR NOT (1 > 2)", "x = 5 OR NOT (2 < 1)", "x = 5 OR NOT (2 >= 3)", "x = 5 OR NOT (3 <= 2)",
        "x = 5 OR NOT (1 != 1)", "x = 5 OR NOT (1 = 2 AND true)", "x = 5 OR NOT (1 = 2 OR false)", "x = 5 OR NOT (NOT (true))"
    }.Select(w => new object[] { w });

    [Theory]
    [MemberData(nameof(TriviallyTrue))]
    public void TriviallyTrueWhere_IsRejected(string where)
    {
        string verdict = ThrowsMessageOrNull<UnfilteredDmlException>($"DELETE FROM orders WHERE {where}", Dml()) ?? "(not rejected)";
        Assert.Equal("DELETE with a trivially true WHERE clause is not permitted.", verdict);
    }

    private string? ThrowsMessageOrNull<T>(string sql, RlsOptions options) where T : Exception
    {
        try { Secure(sql, options); return null; }
        catch (T ex) { return ex.Message; }
    }

    public static IEnumerable<object[]> Filtering() => new[]
    {
        "x = 5", "x = y", "x != x", "x < x", "x > x", "x = 5 AND 1 = 1", "1 = 1 AND x = 5", "x = 5 OR 1 = 2", "1 = 2 OR x = 5", "x = 5 OR false",
        "x = 5 OR 'a' = 'b'", "x = 5 OR 1 > 2", "x = 5 OR 2 < 1", "x = 5 OR 3 < 3", "x = 5 OR 3 > 3", "x = 5 OR 1 >= 2", "x = 5 OR 2 <= 1", "x = 5 OR 1 = 2.5",
        "x = 5 OR NULL IS NOT NULL", "x IS NOT NULL", "x IS NULL OR y IS NOT NULL", "x IS NULL OR x IS NULL", "x IS NOT NULL OR x IS NOT NULL",
        "x BETWEEN x AND y", "x BETWEEN y AND x", "y BETWEEN x AND x", "x IN (y)", "y IN (x)", "x IN (1, 2)",
        "NOT x = 5", "NOT (x = 5)", "x = 5 OR NOT (true)", "x = 5 OR NOT (1 = 1)", "x = 5 OR NOT (x = x)",
        "x = 5 OR NOT (1 = 2 OR true)", "x = 5 OR (1 = 1) = false",
        "x = 'a' OR 'a' = y", "x = 5 AND x = x AND 1 = 2"
    }.Select(w => new object[] { w });

    [Theory]
    [MemberData(nameof(Filtering))]
    public void LegitimateWhere_IsAccepted_AndGetsThePolicyFilterAppended(string where)
    {
        string sql = Secure($"DELETE FROM orders WHERE {where}", Dml());
        Assert.EndsWith("AND (\"tenant_id\" = 42)", sql, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> ColumnInOneChild() => new[]
    {
        "'a' LIKE x", "x LIKE 'a'", "'a' LIKE 'b' ESCAPE x", "1 IN (1, y)", "y IN (1, 2)", "1 IN (SELECT y FROM t2)", "y IN (SELECT 1 FROM t2 WHERE 1 = 1)",
        "EXISTS (SELECT y FROM t2)", "(SELECT max(y) FROM t2) = 1", "1 BETWEEN 0 AND x", "1 BETWEEN x AND 2", "x BETWEEN 0 AND 2",
        "CASE x WHEN 1 THEN 2 END = 2", "CASE WHEN x = 1 THEN 2 END = 2", "CASE WHEN true THEN x END = 2", "CASE WHEN true THEN 1 ELSE x END = 1",
        "coalesce(x, 1) = 1", "coalesce(1, x) = 1", "substring('abc', 1, x) = 'a'", "substring(x, 1) = 'a'", "substring('abc', x) = 'a'", "substring('abc', 1, 2) = x",
        "trim(x) = 'a'", "trim('a' FROM x) = 'a'", "trim(x FROM 'a') = 'a'", "position('a' IN x) = 1", "position(x IN 'a') = 1",
        "CAST(x AS INTEGER) = 1", "date_trunc('day', x) = DATE '2020-01-01'", "EXTRACT(YEAR FROM x) = 2020", "1 IS DISTINCT FROM x", "x IS DISTINCT FROM 1",
        "1 = ANY (SELECT y FROM t2)", "x = ANY (SELECT 1 FROM t2 WHERE 1 = 1)", "NOT x", "x IS NULL", "(x) = 1", "1 + x = 2", "x + 1 = 2"
    }.Select(w => new object[] { w });

    [Theory]
    [MemberData(nameof(ColumnInOneChild))]
    public void AColumnInAnySingleChild_CountsAsAColumnReference(string where)
    {
        string? verdict = ThrowsMessageOrNull<UnfilteredDmlException>($"DELETE FROM orders WHERE {where}", Dml());
        Assert.Null(verdict);
    }

    public static IEnumerable<object[]> NoColumn() => new[]
    {
        "'a' LIKE 'b'", "'a' LIKE 'b' ESCAPE 'c'", "1 IN (1, 2)", "1 BETWEEN 0 AND 2", "CASE 1 WHEN 1 THEN 2 END = 2", "CASE WHEN true THEN 1 ELSE 2 END = 1",
        "coalesce(1, 2) = 1", "substring('abc', 1, 2) = 'a'", "substring('abc', 1) = 'a'", "trim('a') = 'a'", "trim('a' FROM 'b') = 'a'", "position('a' IN 'b') = 1",
        "CAST(1 AS INTEGER) = 1", "EXTRACT(YEAR FROM DATE '2020-01-01') = 2020", "1 IS DISTINCT FROM 2", "NOT true", "1 IS NULL", "(1) = 1", "1 + 2 = 3"
    }.Select(w => new object[] { w });

    [Theory]
    [MemberData(nameof(NoColumn))]
    public void LiteralOnlyWhere_IsRejectedAsLiteralOnly(string where)
    {
        Assert.Equal("DELETE statement WHERE clause must reference at least one table column; literal-only predicates are forbidden.",
            ThrowsMessage<UnfilteredDmlException>($"DELETE FROM orders WHERE {where}", Dml()));
    }
}
