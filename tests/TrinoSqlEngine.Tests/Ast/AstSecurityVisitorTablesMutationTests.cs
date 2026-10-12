namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Collections.Generic;
using System.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;

/// <summary>Mutation-gate tests (M-12) for the table securing of <c>AstSecurityVisitor</c> on the options path (secured subquery, CTE scope, projection rules).</summary>
[Trait("Category", "SqlGate")]
public sealed class AstSecurityVisitorTablesMutationTests
{
    private readonly FastSqlEngine _engine = new();

    private string Secure(string sql, RlsOptions options)
    {
        var (tree, _) = _engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        var ast = new SqlAstBuilder(AstBuilderOptions.FromRlsOptions(options)).BuildStatement(tree);
        var secured = (SqlStatement)new AstSecurityVisitor(options, _engine).Visit(ast);
        return SqlDialectGeneratorFactory.GetGenerator(options.TargetDialect).GenerateSql(secured);
    }

    private static RlsOptions Read() => new()
    {
        PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42"),
        TableColumnsProvider = t => new[] { "id", "name" }
    };

    [Fact]
    public void ACteThatReferencesItself_NeedsWithRecursive()
    {
        var ex = Assert.Throws<SecurityException>(() => Secure("WITH c AS (SELECT id FROM c) SELECT id FROM c", Read()));
        Assert.Equal("A CTE that references itself requires WITH RECURSIVE.", ex.Message);
    }

    [Fact]
    public void ACteReference_IsNotSecuredAsATable()
    {
        string sql = Secure("WITH c AS (SELECT id FROM orders) SELECT id FROM c", Read());
        Assert.Equal("WITH \"c\" AS (SELECT \"id\" FROM (SELECT \"id\" AS \"id\", \"name\" AS \"name\" FROM \"orders\" WHERE \"tenant_id\" = 42) AS \"orders\") SELECT \"id\" FROM \"c\"", sql);
    }

    [Fact]
    public void ATableWithAPolicy_BecomesAProjectedFilteredSubquery()
    {
        Assert.Equal("SELECT \"id\" FROM (SELECT \"id\" AS \"id\", \"name\" AS \"name\" FROM \"orders\" WHERE \"tenant_id\" = 42) AS \"orders\"",
            Secure("SELECT id FROM orders", Read()));
        Assert.Equal("SELECT \"id\" FROM (SELECT \"id\" AS \"id\", \"name\" AS \"name\" FROM \"orders\" WHERE \"tenant_id\" = 42) AS \"o\"",
            Secure("SELECT id FROM orders o", Read()));
    }

    [Fact]
    public void ACorrelatedPolicy_GetsTheTargetAliasOnTheInnerTable()
    {
        var o = Read();
        o.PolicyProvider = new DefaultRlsPolicyProvider("EXISTS (SELECT 1 FROM grants g WHERE g.id = autheris_target.id)");
        Assert.Contains("FROM \"orders\" AS \"autheris_target\" WHERE", Secure("SELECT id FROM orders", o), StringComparison.Ordinal);
        var plain = Read();
        Assert.DoesNotContain("autheris_target", Secure("SELECT id FROM orders", plain), StringComparison.Ordinal);
    }

    [Fact]
    public void ATableWithoutAPolicy_StaysAsIs_UnlessMaskedOrCatalogProjected()
    {
        var o = Read();
        o.PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42", predicate: t => false);
        Assert.Equal("SELECT \"id\" FROM \"orders\"", Secure("SELECT id FROM orders", o));

        // catalog projection wraps it in a projecting subquery without a filter
        o.EnforceCatalogProjection = true;
        Assert.Equal("SELECT \"id\" FROM (SELECT \"id\" AS \"id\", \"name\" AS \"name\" FROM \"orders\") AS \"orders\"", Secure("SELECT id FROM orders", o));

        // ... only for a table with a known column list
        o.TableColumnsProvider = t => Array.Empty<string>();
        Assert.Equal("SELECT \"id\" FROM \"orders\"", Secure("SELECT id FROM orders", o));
        o.TableColumnsProvider = t => null;
        Assert.Equal("SELECT \"id\" FROM \"orders\"", Secure("SELECT id FROM orders", o));
        o.TableColumnsProvider = null;
        Assert.Equal("SELECT \"id\" FROM \"orders\"", Secure("SELECT id FROM orders", o));

        // ... and only when the option is on
        var off = Read();
        off.PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42", predicate: t => false);
        off.EnforceCatalogProjection = false;
        Assert.Equal("SELECT \"id\" FROM \"orders\"", Secure("SELECT id FROM orders", off));
    }

    [Fact]
    public void AMaskedTable_GetsItsMaskExpressionVerbatim()
    {
        var o = Read();
        o.TableColumnsProvider = t => new[] { "id", "ssn" };
        o.ColumnMaskingProvider = new DefaultColumnMaskingPolicyProvider((t, c) => c == "ssn", (t, c) => "'***'");
        Assert.Equal("SELECT \"id\" FROM (SELECT \"id\" AS \"id\", '***' AS \"ssn\" FROM \"orders\" WHERE \"tenant_id\" = 42) AS \"orders\"", Secure("SELECT id FROM orders", o));

        // without a masking provider the same column is projected as is
        var plain = Read();
        plain.TableColumnsProvider = t => new[] { "id", "ssn" };
        Assert.Contains("\"ssn\" AS \"ssn\"", Secure("SELECT id FROM orders", plain), StringComparison.Ordinal);
    }

    [Fact]
    public void AMaskedTableWithoutAColumnList_FailsClosed()
    {
        var o = Read();
        o.TablesWithMaskedColumns.Add("orders");
        o.ColumnMaskingProvider = new DefaultColumnMaskingPolicyProvider((t, c) => c == "ssn", (t, c) => "'***'");
        o.TableColumnsProvider = t => Array.Empty<string>();
        Assert.Equal("Table 'orders' contains masked columns but column provider returned empty schema.", Assert.Throws<SecurityException>(() => Secure("SELECT id FROM orders", o)).Message);
        o.TableColumnsProvider = t => null;
        Assert.Equal("Table 'orders' contains masked columns but column provider returned empty schema.", Assert.Throws<SecurityException>(() => Secure("SELECT id FROM orders", o)).Message);
        o.TableColumnsProvider = null;
        Assert.Equal("Table 'orders' contains masked columns but no TableColumnsProvider was configured to expand the projection.",
            Assert.Throws<SecurityException>(() => Secure("SELECT id FROM orders", o)).Message);
    }

    [Fact]
    public void ATableWithoutAColumnListAndWithoutMasks_IsProjectedWithAWildcard()
    {
        var o = Read();
        o.TableColumnsProvider = null;
        Assert.Equal("SELECT \"id\" FROM (SELECT * FROM \"orders\" WHERE \"tenant_id\" = 42) AS \"orders\"", Secure("SELECT id FROM orders", o));
        o.TableColumnsProvider = t => Array.Empty<string>();
        Assert.Equal("SELECT \"id\" FROM (SELECT * FROM \"orders\" WHERE \"tenant_id\" = 42) AS \"orders\"", Secure("SELECT id FROM orders", o));
        o.TableColumnsProvider = t => null;
        Assert.Equal("SELECT \"id\" FROM (SELECT * FROM \"orders\" WHERE \"tenant_id\" = 42) AS \"orders\"", Secure("SELECT id FROM orders", o));
    }

    [Fact]
    public void ADialectThatQuotesByDefault_KeepsTheTableNameAsTheAlias()
    {
        var o = Read();
        o.TargetDialect = TargetSqlDialect.SqlServer;
        Assert.Equal("SELECT [id] FROM (SELECT [id] AS [id], [name] AS [name] FROM [orders] WHERE [tenant_id] = 42) AS [orders]", Secure("SELECT id FROM orders", o));
    }
}
