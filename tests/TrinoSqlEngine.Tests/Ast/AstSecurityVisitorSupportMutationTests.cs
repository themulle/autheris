namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;

/// <summary>Mutation-gate tests (M-12) for the CTE scope rules, the row limit clamp and the audit anonymizer of <c>AstSecurityVisitor</c>.</summary>
[Trait("Category", "SqlGate")]
public sealed class AstSecurityVisitorSupportMutationTests
{
    private readonly FastSqlEngine _engine = new();

    private string Secure(string sql, RlsOptions options)
    {
        var (tree, _) = _engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        var ast = new SqlAstBuilder(AstBuilderOptions.FromRlsOptions(options)).BuildStatement(tree);
        var secured = (SqlStatement)new AstSecurityVisitor(options, _engine).Visit(ast);
        return SqlDialectGeneratorFactory.GetGenerator(options.TargetDialect).GenerateSql(secured);
    }

    private static RlsOptions Read(long maxRows = 0) => new()
    {
        PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42"),
        TableColumnsProvider = t => new[] { "id" },
        EnforcedMaxRows = maxRows
    };

    [Fact]
    public void CteNamesThatDifferOnlyInCase_AreAmbiguous()
    {
        var ex = Assert.Throws<SecurityException>(() => Secure("WITH \"c\" AS (SELECT 1 AS x), \"C\" AS (SELECT 2 AS x) SELECT x FROM \"c\"", Read()));
        Assert.Equal("CTE names that differ only in case are ambiguous and are not permitted.", ex.Message);
        Assert.Contains("WITH", Secure("WITH c AS (SELECT 1 AS x), d AS (SELECT 2 AS x) SELECT x FROM c", Read()), StringComparison.Ordinal);
    }

    [Fact]
    public void ARecursiveCte_MayNotUseTheNameOfAPolicyTable()
    {
        var ex = Assert.Throws<SecurityException>(() => Secure(
            "WITH RECURSIVE orders AS (SELECT 1 AS id UNION ALL SELECT id + 1 FROM orders WHERE id < 3) SELECT id FROM orders", Read()));
        Assert.Equal("A recursive CTE must not have the name of a catalog table; the reference would be ambiguous.", ex.Message);

        var o = Read();
        o.PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42", predicate: t => t == "orders");
        Assert.Contains("RECURSIVE", Secure("WITH RECURSIVE nums AS (SELECT 1 AS id UNION ALL SELECT id + 1 FROM nums WHERE id < 3) SELECT id FROM nums", o), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT id FROM t", 10, "LIMIT 10")]
    [InlineData("SELECT id FROM t LIMIT 100", 10, "LIMIT 10")]
    [InlineData("SELECT id FROM t LIMIT 5", 10, "LIMIT 5")]
    [InlineData("SELECT id FROM t LIMIT 10", 10, "LIMIT 10")]
    [InlineData("SELECT id FROM t LIMIT 11", 10, "LIMIT 10")]
    [InlineData("SELECT id FROM t OFFSET 3 LIMIT 100", 10, "LIMIT 10 OFFSET 3")]
    public void TheEnforcedRowLimit_ClampsTheRootSelect(string sql, long max, string expectedLimit)
    {
        string result = Secure(sql, Read(max));
        Assert.EndsWith(expectedLimit, result.TrimEnd(), StringComparison.Ordinal);
        if (sql.Contains("OFFSET", StringComparison.Ordinal)) Assert.Contains("OFFSET 3", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEnforcedRowLimit_DoesNotTouchSubqueries_OrApplyWhenDisabled()
    {
        string nested = Secure("SELECT id FROM (SELECT id FROM t LIMIT 500) s", Read(10));
        Assert.Contains("LIMIT 500", nested, StringComparison.Ordinal);
        Assert.EndsWith("LIMIT 10", nested.TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT", Secure("SELECT id FROM t", Read(0)), StringComparison.Ordinal);
        Assert.EndsWith("LIMIT 500", Secure("SELECT id FROM t LIMIT 500", Read(0)).TrimEnd(), StringComparison.Ordinal);
    }

    // ---- AU-06: literal anonymization of audited SQL ----

    [Fact]
    public void AnonymizeSqlForAudit_ReplacesLiteralsAndHashesTheOriginal()
    {
        const string sql = "SELECT id FROM t WHERE name = 'Alice' AND age > 42 AND score < 1.5 AND code = $$x$$ AND u = U&'d\\0061'";
        var (redacted, hash) = AstSecurityVisitor.AnonymizeSqlForAudit(sql);
        Assert.Equal("SELECT id FROM t WHERE name = @p_redacted AND age > @p_redacted AND score < @p_redacted AND code = @p_redacted AND u = @p_redacted", redacted);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))), hash);
        Assert.DoesNotContain("Alice", redacted, StringComparison.Ordinal);
        Assert.Equal("@p_redacted", AstSecurityVisitor.RedactedLiteralPlaceholder);
        Assert.Equal(4096, AstSecurityVisitor.DefaultMaxAuditSqlLength);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnonymizeSqlForAudit_EmptyInput_IsEmptyWithTheEmptyHash(string? sql)
    {
        var (redacted, hash) = AstSecurityVisitor.AnonymizeSqlForAudit(sql);
        Assert.Equal(string.Empty, redacted);
        Assert.Equal("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", hash);
    }

    [Fact]
    public void AnonymizeSqlForAudit_KeepsIdentifiersAndTruncatesLongText()
    {
        var (kept, _) = AstSecurityVisitor.AnonymizeSqlForAudit("SELECT a1, b2 FROM t3");
        Assert.Equal("SELECT a1, b2 FROM t3", kept);

        string longSql = "SELECT " + string.Join(", ", Enumerable.Range(0, 40).Select(i => $"col{i}")) + " FROM t";
        var (full, _) = AstSecurityVisitor.AnonymizeSqlForAudit(longSql, longSql.Length);
        Assert.Equal(longSql, full);
        var (cut, _) = AstSecurityVisitor.AnonymizeSqlForAudit(longSql, 20);
        Assert.Equal(longSql[..20] + "...[TRUNCATED]", cut);
        var (justUnder, _) = AstSecurityVisitor.AnonymizeSqlForAudit(longSql, longSql.Length + 1);
        Assert.Equal(longSql, justUnder);
        var (oneShort, _) = AstSecurityVisitor.AnonymizeSqlForAudit(longSql, longSql.Length - 1);
        Assert.EndsWith("...[TRUNCATED]", oneShort, StringComparison.Ordinal);
    }

    [Fact]
    public void AnonymizeSqlForAudit_UnparsableText_FallsBackToRegexRedaction()
    {
        var (redacted, _) = AstSecurityVisitor.AnonymizeSqlForAudit("name = 'O''Brien' AND n = 42 AND price >= 3.14 AND x = y2");
        Assert.DoesNotContain("Brien", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("42", redacted, StringComparison.Ordinal);
        Assert.Contains("y2", redacted, StringComparison.Ordinal);
    }
}
