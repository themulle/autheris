namespace TrinoSqlEngine.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using TrinoSqlEngine.Analysis;
using Xunit;

[Trait("Category", "SqlGate")]
public class AnalysisAndMaskingTests
{
    private readonly FastSqlEngine _engine = new();

    [Fact]
    public void Analyze_IdentifiesSelectStatement_AndExtractsTables()
    {
        string sql = "SELECT u.id, u.name, o.total FROM users u JOIN orders o ON u.id = o.user_id WHERE u.active = 1";
        var meta = _engine.Analyze(sql.AsMemory());

        Assert.Equal(SqlStatementType.Select, meta.StatementType);
        Assert.Equal(2, meta.ReferencedTables.Count);
        Assert.Equal(1, meta.JoinCount);
        Assert.False(meta.HasExplicitLimit);

        var userTarget = meta.ReferencedTables.First(t => t.TableName == "users");
        Assert.Equal("u", userTarget.Alias);

        var orderTarget = meta.ReferencedTables.First(t => t.TableName == "orders");
        Assert.Equal("o", orderTarget.Alias);
    }

    [Fact]
    public void Analyze_IgnoresCteNames_AndExtractsOnlyPhysicalTables()
    {
        string sql = @"
            WITH cte_summary AS (
                SELECT department_id, COUNT(*) as cnt FROM employees GROUP BY department_id
            ),
            cte_filtered AS (
                SELECT * FROM cte_summary WHERE cnt > 10
            )
            SELECT d.name, f.cnt 
            FROM departments d 
            JOIN cte_filtered f ON d.id = f.department_id";

        var meta = _engine.Analyze(sql.AsMemory());

        Assert.Equal(SqlStatementType.Select, meta.StatementType);
        Assert.Equal(2, meta.ReferencedTables.Count);

        var tableNames = meta.ReferencedTables.Select(t => t.TableName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("employees", tableNames);
        Assert.Contains("departments", tableNames);
        Assert.DoesNotContain("cte_summary", tableNames);
        Assert.DoesNotContain("cte_filtered", tableNames);
    }

    [Fact]
    public void Analyze_DetectsDmlAndDdlStatementTypes()
    {
        Assert.Equal(SqlStatementType.Insert, _engine.Analyze("INSERT INTO audit_log (msg) VALUES ('test')".AsMemory()).StatementType);
        Assert.Equal(SqlStatementType.Update, _engine.Analyze("UPDATE users SET name = 'Alice' WHERE id = 1".AsMemory()).StatementType);
        Assert.Equal(SqlStatementType.Delete, _engine.Analyze("DELETE FROM sessions WHERE expired = 1".AsMemory()).StatementType);
        Assert.Equal(SqlStatementType.Ddl, _engine.Analyze("DROP TABLE evil_table".AsMemory()).StatementType);
        Assert.Equal(SqlStatementType.Ddl, _engine.Analyze("CREATE TABLE new_tbl (id INT)".AsMemory()).StatementType);
        Assert.Equal(SqlStatementType.Ddl, _engine.Analyze("GRANT SELECT ON tbl TO role1".AsMemory()).StatementType);
    }

    [Fact]
    public void Analyze_DetectsTopLevelLimit()
    {
        string sql = "SELECT * FROM products ORDER BY price DESC LIMIT 50";
        var meta = _engine.Analyze(sql.AsMemory());

        Assert.True(meta.HasExplicitLimit);
        Assert.Equal(50, meta.ExplicitLimitValue);
    }

    // The string masking provider test (RewriteRls_WithColumnMasking_PushesDownMaskExpressions) is replaced by the typed mask tests
    // (Compiler/MaskTests: TypedInjection_ProjectsMaskExpression_ForMaskedColumns_AndCollectsValues and the per-kind mask tests).

    private static string CompileWithMaxRows(string sql)
    {
        var env = new TrinoSqlEngine.Tests.Security.ProbeEnv(TargetSqlDialect.PostgreSql);
        return env.Engine.Compile(sql.AsMemory(), env.Request(enforcedMaxRows: 100), System.Threading.CancellationToken.None).Sql.Trim();
    }

    [Fact]
    public void Compile_WithEnforcedMaxRows_AppendsLimitWhenMissing()
    {
        Assert.EndsWith("LIMIT 100", CompileWithMaxRows("SELECT * FROM orders"));
    }

    [Fact]
    public void Compile_WithEnforcedMaxRows_ClampsExistingLimitWhenTooHigh()
    {
        string sql = CompileWithMaxRows("SELECT * FROM orders LIMIT 10000");

        Assert.DoesNotContain("10000", sql);
        Assert.EndsWith("LIMIT 100", sql);
    }

    [Fact]
    public void Compile_WithEnforcedMaxRows_LeavesSmallerLimitUnchanged()
    {
        Assert.EndsWith("LIMIT 25", CompileWithMaxRows("SELECT * FROM orders LIMIT 25"));
    }

    private static HashSet<string> TableNames(SqlQueryMetadata meta) =>
        meta.ReferencedTables.Select(t => t.TableName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Analyze_RecursiveCte_SelfReferenceIsNotAReferencedTable()
    {
        var meta = _engine.Analyze("WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT n FROM r".AsMemory());

        Assert.Empty(meta.ReferencedTables);
        Assert.True(meta.HasRecursiveCte);
        Assert.False(meta.HasNonRecursiveSelfReference);
    }

    [Fact]
    public void Analyze_RecursiveCte_BaseTablesInAnchorAndRecursiveMemberAreReferenced()
    {
        var meta = _engine.Analyze(
            "WITH RECURSIVE r(id) AS (SELECT id FROM orders UNION ALL SELECT e.id FROM entitlements e JOIN r ON e.parent_id = r.id) SELECT id FROM r".AsMemory());

        Assert.Equal(new[] { "entitlements", "orders" }, TableNames(meta).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("r", TableNames(meta));
    }

    [Fact]
    public void Analyze_NonRecursiveCte_SelfReferenceStaysATableAndIsFlagged()
    {
        var chain = _engine.Analyze("WITH chain AS (SELECT id FROM chain) SELECT id FROM chain".AsMemory());
        Assert.Contains("chain", TableNames(chain));
        Assert.True(chain.HasNonRecursiveSelfReference);
        Assert.False(chain.HasRecursiveCte);

        var shadow = _engine.Analyze("WITH orders AS (SELECT id FROM orders) SELECT id FROM orders".AsMemory());
        Assert.Contains("orders", TableNames(shadow));
        Assert.True(shadow.HasNonRecursiveSelfReference);

        // Shadowed by an inner definition, or a plain reference to an earlier CTE: no self-reference.
        Assert.False(_engine.Analyze("WITH a AS (WITH a AS (SELECT 1 AS id) SELECT id FROM a) SELECT id FROM a".AsMemory()).HasNonRecursiveSelfReference);
        Assert.False(_engine.Analyze("WITH a AS (SELECT id FROM orders), b AS (SELECT id FROM a) SELECT id FROM b".AsMemory()).HasNonRecursiveSelfReference);
    }

    [Fact]
    public void Analyze_RecursiveCte_CaseVariantSelfReferenceIsInScope()
    {
        foreach (string sql in new[]
        {
            "WITH RECURSIVE R(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT n FROM R",
            "WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM \"R\" WHERE n < 3) SELECT n FROM r"
        })
        {
            var meta = _engine.Analyze(sql.AsMemory());
            if (sql.Contains("\"R\"", StringComparison.Ordinal))
            {
                // A delimited name is compared exactly (INV-11): "R" is not r, so it is a table (the compiler then rejects it).
                Assert.Contains("R", TableNames(meta));
            }
            else
            {
                Assert.Empty(meta.ReferencedTables);
            }
        }
    }

    [Fact]
    public void Analyze_RecursiveCteName_DoesNotLeakIntoSiblingSubqueryWith()
    {
        var meta = _engine.Analyze(
            ("SELECT a.n FROM (WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r WHERE n < 3) SELECT n FROM r) a, " +
             "(WITH q AS (SELECT 1 AS n) SELECT n FROM r) b").AsMemory());

        Assert.Contains("r", TableNames(meta));
    }

    [Fact]
    public void Analyze_MultipleRecursiveCtes_EachSeesItselfAndEarlierNames()
    {
        var meta = _engine.Analyze(
            ("WITH RECURSIVE a(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM a WHERE n < 2), " +
             "b(n) AS (SELECT n FROM a UNION ALL SELECT n + 1 FROM b WHERE n < 3) SELECT n FROM b").AsMemory());
        Assert.Empty(meta.ReferencedTables);

        // No forward visibility: a refers to b before b is defined, so b is a table there.
        var forward = _engine.Analyze("WITH RECURSIVE a(n) AS (SELECT n FROM b), b(n) AS (SELECT 1) SELECT n FROM a".AsMemory());
        Assert.Contains("b", TableNames(forward));
    }
}
