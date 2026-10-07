namespace TrinoSqlEngine.Tests.Ast.Visitors;

using System;
using System.Collections.Generic;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;

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
    public void ContradictionDetection_DisjointTenantEquality_CollapsesToFalse()
    {
        // Query specifying two different equality values on the same column in AND
        var stmt = SimplifySql("SELECT id FROM orders WHERE tenant_id = 't1' AND tenant_id = 't2'");
        var sql = GenerateSql(stmt);

        Assert.Contains("1 = 0", sql);
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

    [Fact]
    public void EndToEnd_GenerateGovernedSql_IncorporatesSimplifier_DmlStatement()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 't1'"),
            EnforceReadOnlyQueries = false,
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        // DML statement containing 1 = 1 and user filter; policy provider injects matching filter
        string sql = "DELETE FROM orders WHERE 1 = 1 AND tenant_id = 't1'";
        string result = _engine.GenerateGovernedSql(sql, options);

        // 1 = 1 should be gone, duplicate tenant_id filter deduplicated
        Assert.DoesNotContain("1 = 1", result);
        Assert.Contains("\"tenant_id\" = 't1'", result);

        int count = 0;
        int idx = 0;
        while ((idx = result.IndexOf("\"tenant_id\" = 't1'", idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += 18;
        }

        Assert.Equal(1, count);
    }

    [Fact]
    public void EndToEnd_GenerateGovernedSql_IncorporatesSimplifier_SelectStatement()
    {
        var options = new RlsOptions
        {
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        // Query containing 1 = 1 and duplicate filters in WHERE clause
        string sql = "SELECT id FROM orders WHERE 1 = 1 AND tenant_id = 't1' AND tenant_id = 't1'";
        string result = _engine.GenerateGovernedSql(sql, options);

        // 1 = 1 should be gone, duplicate tenant_id filter deduplicated
        Assert.DoesNotContain("1 = 1", result);
        Assert.Contains("\"tenant_id\" = 't1'", result);

        int count = 0;
        int idx = 0;
        while ((idx = result.IndexOf("\"tenant_id\" = 't1'", idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += 18;
        }

        Assert.Equal(1, count);
    }
}
