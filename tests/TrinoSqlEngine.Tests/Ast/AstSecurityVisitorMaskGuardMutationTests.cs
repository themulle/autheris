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
/// Mutation-gate tests (M-12) for the masked column guards of <c>AstSecurityVisitor</c> (predicates, DML and join conditions on the
/// options path): the traversal must find a masked column in every expression and query position, the table and qualifier matching
/// must follow the provider, and each rejection names the right clause.
/// </summary>
[Trait("Category", "SqlGate")]
public sealed class AstSecurityVisitorMaskGuardMutationTests
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

    private static RlsOptions Masked(Func<string, string, bool>? has = null, bool dml = true) => new()
    {
        EnforceReadOnlyQueries = !dml,
        EnforceWithCheckOption = false,
        PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42"),
        ColumnMaskingProvider = new DefaultColumnMaskingPolicyProvider(has ?? ((t, c) => string.Equals(c, "ssn", StringComparison.OrdinalIgnoreCase)), (t, c) => "'***'"),
        TableColumnsProvider = t => new[] { "id", "name", "ssn" }
    };

    private static RlsOptions MaskedOnTable(string table, string column) =>
        Masked((t, c) => string.Equals(t, table, StringComparison.OrdinalIgnoreCase) && string.Equals(c, column, StringComparison.OrdinalIgnoreCase));

    private string Rejected(string sql, RlsOptions o) => Assert.Throws<SecurityException>(() => Secure(sql, o)).Message;

    public static IEnumerable<object[]> MaskedPositions() => new[]
    {
        "ssn = 'a'", "'a' = ssn", "(ssn = 'a')", "NOT ssn = 'a'", "ssn IS NULL", "ssn IS NOT NULL", "CAST(ssn AS VARCHAR) = 'a'",
        "id LIKE ssn", "ssn LIKE 'a%'", "name LIKE 'a' ESCAPE ssn", "id IS DISTINCT FROM ssn", "ssn IS DISTINCT FROM id",
        "id BETWEEN 1 AND ssn", "id BETWEEN ssn AND 2", "ssn BETWEEN 1 AND 2", "id IN (1, ssn)", "ssn IN (1, 2)",
        "id IN (SELECT ssn FROM t2)", "ssn IN (SELECT 1 FROM t2)", "(SELECT ssn FROM t2) = 1", "EXISTS (SELECT 1 FROM t2 WHERE ssn = 1)",
        "id = ANY (SELECT ssn FROM t2)", "ssn = ANY (SELECT 1 FROM t2)",
        "abs(ssn) = 1", "coalesce(1, ssn) = 1", "CASE ssn WHEN 1 THEN 2 END = 2", "CASE WHEN ssn = 1 THEN 2 END = 2", "CASE WHEN id = 1 THEN ssn END = 2",
        "CASE WHEN id = 1 THEN 2 ELSE ssn END = 2", "EXTRACT(YEAR FROM ssn) = 2020", "substring(ssn, 1, 2) = 'a'", "substring(name, ssn, 2) = 'a'",
        "substring(name, 1, ssn) = 'a'", "trim(ssn) = 'a'", "trim(ssn FROM name) = 'a'", "trim(name FROM ssn) = 'a'", "position('a' IN ssn) = 1", "position(ssn IN name) = 1",
        "date_trunc('day', ssn) = DATE '2020-01-01'", "(id, ssn) = (1, 2)", "ARRAY[1, ssn] = ARRAY[1, 2]",
        "id IN (SELECT 1 FROM t2 ORDER BY ssn)", "id IN (WITH c AS (SELECT ssn FROM t2) SELECT 1 FROM c)",
        "id IN (SELECT 1 FROM t2 GROUP BY ssn)", "id IN (SELECT 1 FROM t2 GROUP BY GROUPING SETS ((ssn), ()))", "id IN (SELECT 1 FROM t2 GROUP BY id HAVING max(ssn) > 1)",
        "id IN (SELECT 1 UNION SELECT ssn FROM t2)", "id IN (SELECT ssn FROM t2 UNION SELECT 1)", "id IN (SELECT 1 FROM t2 a JOIN t3 b ON a.id = ssn)",
        "id IN (SELECT 1 FROM (SELECT ssn FROM t2) s)", "id IN (SELECT 1 FROM t2 a JOIN t3 b USING (ssn))",
        "id IN (SELECT 1 FROM t2 LIMIT 1)".Replace("LIMIT 1", "WHERE ssn = 1 LIMIT 1"), "id IN (VALUES (1), (ssn))",
        "id > (SELECT max(id) FROM t2 WHERE ssn IS NULL)"
    }.Select(w => new object[] { w });

    [Theory]
    [MemberData(nameof(MaskedPositions))]
    public void ADeleteWhereWithAMaskedColumnAnywhere_IsRejected(string where)
    {
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in DELETE WHERE.", Rejected($"DELETE FROM orders WHERE {where}", Masked()));
    }

    [Theory]
    [MemberData(nameof(MaskedPositions))]
    public void ASelectWhereWithAMaskedColumnAnywhere_IsRejected(string where)
    {
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in WHERE.", Rejected($"SELECT id FROM orders WHERE {where}", Masked(dml: false)));
    }

    [Theory]
    [InlineData("id IN (SELECT 1 FROM t2 a JOIN t3 b USING (ssn))")]
    [InlineData("name LIKE 'a'")]
    public void TheSameShapesWithoutAMaskedColumn_ArePermitted(string where)
    {
        string guarded = where.Contains("ssn", StringComparison.Ordinal) ? where.Replace("ssn", "name", StringComparison.Ordinal) : where;
        Assert.EndsWith("AND (\"tenant_id\" = 42)", Secure($"DELETE FROM orders WHERE id = 1 AND {guarded}", Masked()), StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateSetAndWhere_AreGuarded_WithTheirOwnClauseNames()
    {
        var o = Masked();
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in UPDATE SET.", Rejected("UPDATE orders SET name = 'a', ssn = 'b' WHERE id = 1", o));
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in UPDATE SET.", Rejected("UPDATE orders SET name = ssn WHERE id = 1", o));
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in UPDATE SET.", Rejected("UPDATE orders SET name = abs(1) + ssn WHERE id = 1", o));
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in UPDATE WHERE.", Rejected("UPDATE orders SET name = 'a' WHERE ssn = 'x'", o));
        Assert.EndsWith("AND (\"tenant_id\" = 42)", Secure("UPDATE orders SET name = 'a' WHERE id = 1", o), StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuards_CanBeSwitchedOff_OrNeedAProvider()
    {
        var off = Masked();
        off.RejectMaskedColumnsInDml = false;
        Assert.EndsWith("AND (\"tenant_id\" = 42)", Secure("DELETE FROM orders WHERE ssn = 'x'", off), StringComparison.Ordinal);
        Assert.EndsWith("AND (\"tenant_id\" = 42)", Secure("UPDATE orders SET name = ssn WHERE ssn = 'x'", off).Replace("\"ssn\" = 'b'", "x", StringComparison.Ordinal), StringComparison.Ordinal);

        var noProvider = Masked();
        noProvider.ColumnMaskingProvider = null;
        noProvider.TableColumnsProvider = null;
        Assert.EndsWith("AND (\"tenant_id\" = 42)", Secure("DELETE FROM orders WHERE ssn = 'x'", noProvider), StringComparison.Ordinal);
        Assert.Contains("SELECT", Secure("SELECT id FROM orders WHERE ssn = 'x'", noProvider), StringComparison.Ordinal);

        var predicatesOff = Masked(dml: false);
        predicatesOff.RejectMaskedColumnsInPredicates = false;
        Assert.Contains("ssn", Secure("SELECT id FROM orders WHERE ssn = 'x'", predicatesOff), StringComparison.Ordinal);
    }

    [Fact]
    public void SelectPredicates_NameTheirClause()
    {
        var o = Masked(dml: false);
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in HAVING.", Rejected("SELECT id FROM orders GROUP BY id HAVING max(ssn) > 1", o));
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in ORDER BY.", Rejected("SELECT id FROM orders ORDER BY ssn", o));
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in ORDER BY.", Rejected("SELECT id FROM orders UNION SELECT id FROM orders ORDER BY ssn", o));
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in JOIN condition.", Rejected("SELECT o.id FROM orders o JOIN t2 ON o.ssn = t2.id", o));
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in JOIN condition.", Rejected("SELECT o.id FROM orders o JOIN t2 USING (ssn)", o));
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in JOIN condition.",
            Rejected("SELECT o.id FROM orders o JOIN t2 ON o.id = t2.id JOIN t3 ON t3.id = o.ssn", o));
        Assert.Equal("Masked column 'ssn' of table 't3' must not be referenced in JOIN condition.",
            Rejected("SELECT o.id FROM t3 JOIN (orders o JOIN t2 ON o.ssn = t2.id) ON t3.id = o.id", o));
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in WHERE.",
            Rejected("SELECT id FROM (SELECT id FROM orders WHERE ssn = 'x') s", o));
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in WHERE.", Rejected("SELECT id FROM orders WHERE ssn = 'x' UNION SELECT id FROM t2", o));
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in WHERE.", Rejected("SELECT id FROM t2 UNION SELECT id FROM orders WHERE ssn = 'x'", o));
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in WHERE.", Rejected("SELECT id FROM orders WHERE id IN (SELECT id FROM t2) AND ssn = 'x'", o));
    }

    [Fact]
    public void ASelectWithoutFrom_OrWithoutMaskedReferences_Passes()
    {
        var o = Masked(dml: false);
        Assert.Contains("1", Secure("SELECT 1", o), StringComparison.Ordinal);
        Assert.Contains("\"name\"", Secure("SELECT name FROM orders WHERE id = 1 ORDER BY id", o), StringComparison.Ordinal);
    }

    // ---- which table a masked column belongs to ----

    [Fact]
    public void TheQualifier_IsAskedAsATableToo()
    {
        var o = MaskedOnTable("other", "ssn");
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in DELETE WHERE.", Rejected("DELETE FROM orders WHERE other.ssn = 1", o));
        Assert.EndsWith("AND (\"tenant_id\" = 42)", Secure("DELETE FROM orders WHERE ssn = 1", o), StringComparison.Ordinal);
        Assert.EndsWith("AND (\"tenant_id\" = 42)", Secure("DELETE FROM orders WHERE orders.ssn = 1", o), StringComparison.Ordinal);
    }

    [Fact]
    public void TablesWithMaskedColumns_AreAskedForEveryColumn()
    {
        var o = MaskedOnTable("zzz", "ssn");
        o.TablesWithMaskedColumns.Add("zzz");
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in DELETE WHERE.", Rejected("DELETE FROM orders WHERE ssn = 1", o));
        Assert.Equal("Masked column 'ssn' of table 'orders' must not be referenced in DELETE WHERE.",
            Rejected("DELETE FROM orders WHERE id IN (SELECT 1 FROM a JOIN b USING (ssn))", o));
        Assert.EndsWith("AND (\"tenant_id\" = 42)", Secure("DELETE FROM orders WHERE name = 1", o), StringComparison.Ordinal);
        Assert.EndsWith("AND (\"tenant_id\" = 42)", Secure("DELETE FROM orders WHERE id IN (SELECT 1 FROM a JOIN b USING (name))", o), StringComparison.Ordinal);
    }

    [Fact]
    public void AJoinCondition_IsCheckedAgainstEveryTableOfTheFromClause()
    {
        var o = MaskedOnTable("t2", "secret");
        // the qualifier t2 is asked as a table, so the first table of the FROM list reports it
        Assert.Equal("Masked column 'secret' of table 'orders' must not be referenced in JOIN condition.", Rejected("SELECT o.id FROM orders o JOIN t2 ON t2.secret = o.id", o));
        // a USING column is asked per table of the FROM list; only t2 has the mask
        Assert.Equal("Masked column 'secret' of table 't2' must not be referenced in JOIN condition.", Rejected("SELECT o.id FROM orders o JOIN t2 USING (secret)", o));
        Assert.Equal("Masked column 'secret' of table 'orders' must not be referenced in WHERE.", Rejected("SELECT o.id FROM orders o JOIN t2 ON o.id = t2.id WHERE t2.secret = 1", o));
        Assert.Contains("JOIN", Secure("SELECT o.id FROM orders o JOIN t2 ON o.id = t2.id", o), StringComparison.Ordinal);
    }

    // ---- whole-row references ----

    [Fact]
    public void WholeRowReferences_ToATableWithMaskedColumns_AreRejected()
    {
        var o = Masked();
        Assert.Equal("Whole-row reference to 'orders' in DELETE WHERE is forbidden because table 'orders' contains masked columns.",
            Rejected("DELETE FROM orders WHERE orders IS NOT NULL", o));
        Assert.Equal("Whole-row reference to 'orders' in DELETE WHERE is forbidden because table 'dbo.orders' contains masked columns.",
            Rejected("DELETE FROM dbo.orders WHERE orders IS NOT NULL", o));
        Assert.Equal("Whole-row reference to 'orders' in DELETE WHERE is forbidden because table 'dbo.orders' contains masked columns.",
            Rejected("DELETE FROM dbo.orders WHERE dbo.orders IS NOT NULL", o));
        Assert.Equal("Whole-row reference to 'orders' in UPDATE SET is forbidden because table 'orders' contains masked columns.",
            Rejected("UPDATE orders SET name = orders WHERE id = 1", o));
        Assert.Equal("Whole-row reference to 'orders' in UPDATE WHERE is forbidden because table 'orders' contains masked columns.",
            Rejected("UPDATE orders SET name = 'a' WHERE orders IS NOT NULL", o));
        // an alias of the FROM clause of a read is a whole-row reference as well
        var read = Masked(dml: false);
        Assert.Equal("Whole-row reference to 'o' in WHERE is forbidden because table 'orders' contains masked columns.", Rejected("SELECT id FROM orders o WHERE o IS NOT NULL", read));
        Assert.Equal("Whole-row reference to 'O' in WHERE is forbidden because table 'orders' contains masked columns.", Rejected("SELECT id FROM orders o WHERE O IS NOT NULL", read));
        Assert.Equal("Whole-row reference to 'orders' in WHERE is forbidden because table 'orders' contains masked columns.", Rejected("SELECT id FROM orders WHERE orders IS NOT NULL", read));
    }

    [Fact]
    public void WholeRowReferences_AreAllowedWhenTheSwitchIsOff_OrTheTableHasNoMasks()
    {
        var off = Masked();
        off.RejectWholeRowReferencesInDml = false;
        Assert.EndsWith("AND (\"tenant_id\" = 42)", Secure("DELETE FROM orders WHERE orders IS NOT NULL", off), StringComparison.Ordinal);

        var noMasks = Masked((t, c) => false);
        Assert.EndsWith("AND (\"tenant_id\" = 42)", Secure("DELETE FROM orders WHERE orders IS NOT NULL", noMasks), StringComparison.Ordinal);

        var other = Masked();
        Assert.EndsWith("AND (\"tenant_id\" = 42)", Secure("DELETE FROM orders WHERE other IS NOT NULL", other), StringComparison.Ordinal);
    }

    [Fact]
    public void QualifiedWildcards_OfAMaskedTable_AreRejectedInTheGuardedClause()
    {
        var o = Masked();
        Assert.Equal("Whole-row reference to 'orders.*' in DELETE WHERE is forbidden because table 'orders' contains masked columns.",
            Rejected("DELETE FROM orders WHERE EXISTS (SELECT orders.* FROM t2)", o));
        Assert.Equal("Whole-row reference to 'dbo.orders.*' in DELETE WHERE is forbidden because table 'orders' contains masked columns.",
            Rejected("DELETE FROM orders WHERE EXISTS (SELECT dbo.orders.* FROM t2)", o));
        Assert.Equal("Whole-row reference to 'orders.*' in DELETE WHERE is forbidden because table 'dbo.orders' contains masked columns.",
            Rejected("DELETE FROM dbo.orders WHERE EXISTS (SELECT orders.* FROM t2)", o));
        Assert.Equal("Whole-row reference to 'o2.*' in DELETE WHERE is forbidden because table 'orders' contains masked columns.",
            Rejected("DELETE FROM orders WHERE EXISTS (SELECT o2.* FROM orders o2)", o));
        // the qualifier names another table that has masked columns itself
        var t2Masked = MaskedOnTable("t2", "secret");
        t2Masked.TablesWithMaskedColumns.Add("t2");
        Assert.Equal("Whole-row reference to 't2.*' in DELETE WHERE is forbidden because table 'orders' contains masked columns.",
            Rejected("DELETE FROM orders WHERE EXISTS (SELECT t2.* FROM t2)", t2Masked));
        var t2ByColumns = MaskedOnTable("t2", "secret");
        t2ByColumns.TableColumnsProvider = t => t == "t2" ? new[] { "id", "secret" } : new[] { "id" };
        Assert.Equal("Whole-row reference to 't2.*' in DELETE WHERE is forbidden because table 'orders' contains masked columns.",
            Rejected("DELETE FROM orders WHERE EXISTS (SELECT t2.* FROM t2)", t2ByColumns));
        // an unqualified wildcard, and a wildcard of a table without masks, are not whole-row references of the target
        var onlyOrders = Masked((t, c) => t == "orders" && c == "ssn");
        Assert.EndsWith("AND (\"tenant_id\" = 42)", Secure("DELETE FROM orders WHERE EXISTS (SELECT * FROM t2 WHERE t2.id = orders.id)", onlyOrders), StringComparison.Ordinal);
        Assert.EndsWith("AND (\"tenant_id\" = 42)", Secure("DELETE FROM orders WHERE EXISTS (SELECT t2.* FROM t2 WHERE t2.id = orders.id)", onlyOrders), StringComparison.Ordinal);
    }
}
