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
}
