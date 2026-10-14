using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Security;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate tests (M-12) of the unfiltered-DML check (column reference and trivially true predicates) and of the masked column
/// predicate guard of the security visitor, driven through the typed compiler: the traversal must find a masked column in every
/// expression and query position, and a literal-only or tautological DML condition is rejected for the right reason.
/// </summary>
[Trait("Category", "SqlGate")]
public class TypedGuardMutationTests
{
    private readonly FastSqlEngine _engine = new();
    private readonly DictPolicyProvider _rowFilters = new();
    private readonly DictMaskProvider _masks = new();
    private HashSet<string> _maskedTables = new();

    public TypedGuardMutationTests()
    {
        _rowFilters.NoPolicy.Add(Orders);
        _rowFilters.NoPolicy.Add(Entitlements);
        _rowFilters.NoPolicy.Add(Lookup);
    }

    private void MaskEmail() =>
        _masks.Masks[(Orders, "email")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);

    private CompiledSql Compile(string sql) => _engine.Compile(sql.AsMemory(), new CompileRequest
    {
        TargetDialect = TargetSqlDialect.SqlServer,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        Statements = StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete,
        Policy = new GovernancePolicy
        {
            RowFilters = _rowFilters,
            Masks = _masks,
            Catalog = Catalog(),
            Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String),
            TablesWithMaskedColumns = _maskedTables
        }
    }, CancellationToken.None);

    private string Rejected<T>(string sql) where T : Exception => Assert.Throws<T>(() => Compile(sql)).Message;

    private static IEnumerable<object[]> Rows(params string[] items) => items.Select(i => new object[] { i });

    public static IEnumerable<object[]> TriviallyTrue() => Rows("id = 5 OR 1 = 1", "1 = 1 OR id = 5", "(id = 5 OR true)", "id = 5 OR (true)", "id = 5 OR false OR true",
        "id = id", "ID = id", "orders.id = orders.ID", "id <= id", "id >= id", "id BETWEEN id AND id", "id IN (amount, id)", "id IN (id)",
        "id IS NULL OR id IS NOT NULL", "id IS NOT NULL OR id IS NULL", "(id IS NULL) OR (id IS NOT NULL)", "id IS NULL OR ID IS NOT NULL",
        "id = id AND 1 = 1", "id = id AND true", "(id = id) AND (amount = amount)",
        "NOT (id = 5 AND false)", "NOT (1 = 0) AND id = id", "NOT (id = 5 AND 1 = 2) ",
        "id = 5 OR 2 > 1", "id = 5 OR 1 < 2", "id = 5 OR 1 = 1.0", "id = 5 OR 2 = 2.00", "id = 5 OR NOT (1 <> 1.0)", "id = 5 OR 1 < 1.5",
        "id = 5 OR 9007199254740993 <> 9007199254740992", "id = 5 OR 9007199254740992 < 9007199254740993", "id = 5 OR '1.0' <> '1'", "id = 5 OR 1 != 2", "id = 5 OR 3 <= 3", "id = 5 OR 3 >= 3",
        "id = 5 OR 'a' = 'a'", "id = 5 OR 'a' != 'b'", "id = 5 OR 'a' <= 'a'", "id = 5 OR 'a' >= 'a'", "id = 5 OR 1.5 >= 1", "id = 5 OR 1 <= 1.5",
        "id = 5 OR 1 IS NOT NULL", "id = 5 OR 'a' IS NOT NULL",
        "id = 5 OR NOT (1 = 2)", "id = 5 OR NOT (false)", "id = 5 OR NOT (1 > 2)", "id = 5 OR NOT (2 < 1)", "id = 5 OR NOT (2 >= 3)", "id = 5 OR NOT (3 <= 2)",
        "id = 5 OR NOT (1 != 1)", "id = 5 OR NOT (1 = 2 AND true)", "id = 5 OR NOT (1 = 2 OR false)", "id = 5 OR NOT (NOT (true))");

    public static IEnumerable<object[]> Filtering() => Rows("id = 5", "id = amount", "id != id", "id < id", "id > id", "id = 5 AND 1 = 1", "1 = 1 AND id = 5", "id = 5 OR 1 = 2", "1 = 2 OR id = 5", "id = 5 OR false",
        "id = 5 OR 'a' = 'b'", "id = 5 OR 1 > 2", "id = 5 OR 2 < 1", "id = 5 OR 3 < 3", "id = 5 OR 3 > 3", "id = 5 OR 1 >= 2", "id = 5 OR 2 <= 1", "id = 5 OR 1 = 2.5",
        "id = 5 OR NULL IS NOT NULL", "id IS NOT NULL", "id IS NULL OR amount IS NOT NULL", "id IS NULL OR id IS NULL", "id IS NOT NULL OR id IS NOT NULL",
        "id BETWEEN id AND amount", "id BETWEEN amount AND id", "amount BETWEEN id AND id", "id IN (amount)", "amount IN (id)", "id IN (1, 2)",
        "NOT id = 5", "NOT (id = 5)", "id = 5 OR NOT (true)", "id = 5 OR NOT (1 = 1)", "id = 5 OR NOT (id = id)",
        "id = 5 OR NOT (1 = 2 OR true)", "id = 5 OR (1 = 1) = false",
        "id = 'a' OR 'a' = amount");

    public static IEnumerable<object[]> ColumnInOneChild() => Rows("'a' LIKE id", "id LIKE 'a'", "'a' LIKE 'b' ESCAPE id", "1 IN (1, amount)", "amount IN (1, 2)", "1 IN (SELECT amount FROM entitlements)", "amount IN (SELECT 1 FROM entitlements WHERE 1 = 1)",
        "EXISTS (SELECT amount FROM entitlements)", "(SELECT max(amount) FROM entitlements) = 1", "1 BETWEEN 0 AND id", "1 BETWEEN id AND 2", "id BETWEEN 0 AND 2",
        "CASE id WHEN 1 THEN 2 END = 2", "CASE WHEN id = 1 THEN 2 END = 2", "CASE WHEN true THEN id END = 2", "CASE WHEN true THEN 1 ELSE id END = 1",
        "coalesce(id, 1) = 1", "coalesce(1, id) = 1", "substring('abc', 1, id) = 'a'", "substring(id, 1) = 'a'", "substring('abc', id) = 'a'", "substring('abc', 1, 2) = id",
        "trim(id) = 'a'", "trim('a' FROM id) = 'a'", "trim(id FROM 'a') = 'a'", "position('a' IN id) = 1", "position(id IN 'a') = 1",
        "CAST(id AS INTEGER) = 1", "EXTRACT(YEAR FROM id) = 2020", "1 IS DISTINCT FROM id", "id IS DISTINCT FROM 1",
        "1 = ANY (SELECT amount FROM entitlements)", "id = ANY (SELECT 1 FROM entitlements WHERE 1 = 1)", "NOT id", "id IS NULL", "(id) = 1", "1 + id = 2", "id + 1 = 2");

    public static IEnumerable<object[]> NoColumn() => Rows("'a' LIKE 'b'", "'a' LIKE 'b' ESCAPE 'c'", "1 IN (1, 2)", "1 BETWEEN 0 AND 2", "CASE 1 WHEN 1 THEN 2 END = 2", "CASE WHEN true THEN 1 ELSE 2 END = 1",
        "coalesce(1, 2) = 1", "substring('abc', 1, 2) = 'a'", "substring('abc', 1) = 'a'", "trim('a') = 'a'", "trim('a' FROM 'b') = 'a'", "position('a' IN 'b') = 1",
        "CAST(1 AS INTEGER) = 1", "EXTRACT(YEAR FROM DATE '2020-01-01') = 2020", "1 IS DISTINCT FROM 2", "NOT true", "1 IS NULL", "(1) = 1", "1 + 2 = 3");

    public static IEnumerable<object[]> MaskedPositions() => Rows("email = 'a'", "'a' = email", "(email = 'a')", "NOT email = 'a'", "email IS NULL", "email IS NOT NULL", "CAST(email AS VARCHAR) = 'a'",
        "id LIKE email", "email LIKE 'a%'", "name LIKE 'a' ESCAPE email", "id IS DISTINCT FROM email", "email IS DISTINCT FROM id",
        "id BETWEEN 1 AND email", "id BETWEEN email AND 2", "email BETWEEN 1 AND 2", "id IN (1, email)", "email IN (1, 2)",
        "id IN (SELECT email FROM entitlements)", "email IN (SELECT 1 FROM entitlements)", "(SELECT email FROM entitlements) = 1", "EXISTS (SELECT 1 FROM entitlements WHERE email = 1)",
        "id = ANY (SELECT email FROM entitlements)", "email = ANY (SELECT 1 FROM entitlements)",
        "abs(email) = 1", "coalesce(1, email) = 1", "CASE email WHEN 1 THEN 2 END = 2", "CASE WHEN email = 1 THEN 2 END = 2", "CASE WHEN id = 1 THEN email END = 2",
        "CASE WHEN id = 1 THEN 2 ELSE email END = 2", "EXTRACT(YEAR FROM email) = 2020", "substring(email, 1, 2) = 'a'", "substring(name, email, 2) = 'a'",
        "substring(name, 1, email) = 'a'", "trim(email) = 'a'", "trim(email FROM name) = 'a'", "trim(name FROM email) = 'a'", "position('a' IN email) = 1", "position(email IN name) = 1", "(id, email) = (1, 2)",
        "id IN (SELECT 1 FROM entitlements ORDER BY email)", "id IN (WITH c AS (SELECT email FROM entitlements) SELECT 1 FROM c)",
        "id IN (SELECT 1 FROM entitlements GROUP BY email)", "id IN (SELECT 1 FROM entitlements GROUP BY GROUPING SETS ((email), ()))", "id IN (SELECT 1 FROM entitlements GROUP BY id HAVING max(email) > 1)",
        "id IN (SELECT 1 UNION SELECT email FROM entitlements)", "id IN (SELECT email FROM entitlements UNION SELECT 1)", "id IN (SELECT 1 FROM entitlements a JOIN lookup b ON a.id = email)",
        "id IN (SELECT 1 FROM (SELECT email FROM entitlements) s)", "id IN (SELECT 1 FROM entitlements a JOIN lookup b USING (email))",
        "id IN (SELECT 1 FROM entitlements LIMIT 1)".Replace("LIMIT 1", "WHERE email = 1 LIMIT 1"), "id IN (VALUES (1), (email))",
        "id > (SELECT max(id) FROM entitlements WHERE email IS NULL)");

    [Fact]
    public void UnfilteredDml_MessagesNameTheOperation()
    {
        Assert.Equal("DELETE without a WHERE clause is not permitted.", Rejected<UnfilteredDmlException>("DELETE FROM orders"));
        Assert.Equal("UPDATE without a WHERE clause is not permitted.", Rejected<UnfilteredDmlException>("UPDATE orders SET status = 'x'"));
        Assert.Equal("DELETE statement WHERE clause must reference at least one table column; literal-only predicates are forbidden.", Rejected<UnfilteredDmlException>("DELETE FROM orders WHERE 5 = 6"));
        Assert.Equal("UPDATE statement WHERE clause must reference at least one table column; literal-only predicates are forbidden.", Rejected<UnfilteredDmlException>("UPDATE orders SET status = 'x' WHERE 5 = 6"));
        Assert.Equal("DELETE with a trivially true WHERE clause is not permitted.", Rejected<UnfilteredDmlException>("DELETE FROM orders WHERE id = id"));
        Assert.Equal("UPDATE with a trivially true WHERE clause is not permitted.", Rejected<UnfilteredDmlException>("UPDATE orders SET status = 'x' WHERE id = id"));
    }

    [Theory]
    [MemberData(nameof(TriviallyTrue))]
    public void TriviallyTrueWhere_IsRejected(string where)
    {
        // the simplifier folds a tautology with a constant (x OR true) to a constant first, which is rejected as literal-only
        string message = Rejected<UnfilteredDmlException>($"DELETE FROM orders WHERE {where}");
        Assert.Contains(message, new[]
        {
            "DELETE with a trivially true WHERE clause is not permitted.",
            "DELETE statement WHERE clause must reference at least one table column; literal-only predicates are forbidden."
        });
    }

    [Theory]
    [MemberData(nameof(Filtering))]
    [MemberData(nameof(ColumnInOneChild))]
    public void LegitimateWhere_IsAccepted(string where) => Assert.Equal(SqlStatementClass.Delete, Compile($"DELETE FROM orders WHERE {where}").StatementClass);

    [Theory]
    [MemberData(nameof(NoColumn))]
    public void LiteralOnlyWhere_IsRejectedAsLiteralOnly(string where) =>
        Assert.Equal("DELETE statement WHERE clause must reference at least one table column; literal-only predicates are forbidden.", Rejected<UnfilteredDmlException>($"DELETE FROM orders WHERE {where}"));

    [Theory]
    [MemberData(nameof(MaskedPositions))]
    public void ASelectWhereWithAMaskedColumnAnywhere_IsRejected(string where)
    {
        MaskEmail();
        string message = Rejected<SecurityException>($"SELECT id FROM orders WHERE {where}");
        Assert.Contains("email", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("must not be referenced in WHERE", message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectPredicates_NameTheirClause()
    {
        MaskEmail();
        Assert.EndsWith("in HAVING.", Rejected<SecurityException>("SELECT id FROM orders GROUP BY id HAVING max(email) > 'a'"), StringComparison.Ordinal);
        Assert.EndsWith("in ORDER BY.", Rejected<SecurityException>("SELECT id FROM orders ORDER BY email"), StringComparison.Ordinal);
        Assert.EndsWith("in ORDER BY.", Rejected<SecurityException>("SELECT id FROM orders UNION SELECT id FROM orders ORDER BY email"), StringComparison.Ordinal);
        Assert.EndsWith("in JOIN condition.", Rejected<SecurityException>("SELECT o.id FROM orders o JOIN entitlements e ON o.email = e.id"), StringComparison.Ordinal);
        Assert.EndsWith("in JOIN condition.", Rejected<SecurityException>("SELECT o.id FROM orders o JOIN entitlements e USING (email)"), StringComparison.Ordinal);
        Assert.EndsWith("in JOIN condition.", Rejected<SecurityException>("SELECT o.id FROM orders o JOIN entitlements e ON o.id = e.id JOIN lookup l ON l.code = o.email"), StringComparison.Ordinal);
        Assert.EndsWith("in WHERE.", Rejected<SecurityException>("SELECT id FROM (SELECT id FROM orders WHERE email = 'x') s"), StringComparison.Ordinal);
        Assert.EndsWith("in WHERE.", Rejected<SecurityException>("SELECT id FROM orders WHERE email = 'x' UNION SELECT id FROM entitlements"), StringComparison.Ordinal);
        Assert.EndsWith("in WHERE.", Rejected<SecurityException>("SELECT id FROM entitlements UNION SELECT id FROM orders WHERE email = 'x'"), StringComparison.Ordinal);
        Assert.Equal(SqlStatementClass.Select, Compile("SELECT id FROM orders WHERE id = 1 ORDER BY id").StatementClass);
    }

    [Fact]
    public void WholeRowReferences_ToATableWithMaskedColumns_AreRejectedInAPredicate()
    {
        MaskEmail();
        Assert.Contains("Whole-row reference", Rejected<SecurityException>("SELECT id FROM orders o WHERE o IS NOT NULL"), StringComparison.Ordinal);
        Assert.Contains("Whole-row reference", Rejected<SecurityException>("SELECT id FROM orders WHERE orders IS NOT NULL"), StringComparison.Ordinal);
        Assert.Contains("Whole-row reference", Rejected<SecurityException>("SELECT id FROM orders WHERE EXISTS (SELECT orders.* FROM entitlements)"), StringComparison.Ordinal);
        Assert.Contains("Whole-row reference", Rejected<SecurityException>("SELECT id FROM orders o WHERE EXISTS (SELECT o.* FROM entitlements)"), StringComparison.Ordinal);
        Assert.Equal(SqlStatementClass.Select, Compile("SELECT id FROM orders WHERE EXISTS (SELECT * FROM entitlements e WHERE e.orderid = orders.id)").StatementClass);
    }

    [Fact]
    public void TablesWithMaskedColumns_AreAskedForEveryColumn()
    {
        _masks.Masks[(Entitlements, "orderid")] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
        _maskedTables = new HashSet<string> { "Entitlements" };
        Assert.Contains("orderid", Rejected<SecurityException>("SELECT id FROM orders WHERE orderid = 1"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orderid", Rejected<SecurityException>("SELECT o.id FROM orders o JOIN lookup l USING (orderid)"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheMaskedColumnGuard_NeedsAMask()
    {
        Assert.Equal(SqlStatementClass.Select, Compile("SELECT id FROM orders WHERE email = 'x'").StatementClass);
    }

    [Fact]
    public void CteScopeRules_AreEnforcedWithTheirOwnReasons()
    {
        Assert.Equal("CTE names that differ only in case are ambiguous and are not permitted.",
            Rejected<SecurityException>("WITH \"c\" AS (SELECT 1 AS x), \"C\" AS (SELECT 2 AS x) SELECT x FROM \"c\""));
        Assert.Equal("A recursive CTE must not have the name of a catalog table; the reference would be ambiguous.",
            Rejected<SecurityException>("WITH RECURSIVE orders AS (SELECT 1 AS id UNION ALL SELECT id + 1 FROM orders WHERE id < 3) SELECT id FROM orders"));
    }

    [Fact]
    public void ATableQueryBody_IsReadAsAPlainSelectOfTheSecuredTable()
    {
        string sql = Compile("TABLE entitlements").Sql;
        Assert.DoesNotContain("DISTINCT", sql, StringComparison.Ordinal);
        Assert.Contains("TenantId", sql, StringComparison.Ordinal);
    }
}
