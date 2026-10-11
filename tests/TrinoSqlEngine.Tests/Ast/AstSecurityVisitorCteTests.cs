namespace TrinoSqlEngine.Tests.Ast;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;

public sealed class AstSecurityVisitorCteTests
{
    private readonly FastSqlEngine _engine = new();

    private string SecureAndGenerate(string sql, RlsOptions options)
    {
        var (tree, _) = _engine.Parse(sql.AsMemory(), SqlTokenSecurityOptions.None);
        var builder = new SqlAstBuilder(AstBuilderOptions.FromRlsOptions(options));
        var ast = builder.BuildStatement(tree);

        var visitor = new AstSecurityVisitor(options, _engine);
        var secured = (SqlStatement)visitor.Visit(ast);

        var generator = SqlDialectGeneratorFactory.GetGenerator(options.TargetDialect);
        return generator.GenerateSql(secured);
    }

    [Fact]
    public void Cte_ExitTiming_SelfNamedReferenceInNonRecursiveWith_IsRejected()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42"),
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        // CR-ADG-32: a self-reference needs WITH RECURSIVE; it is never silently a physical scan
        Assert.Throws<System.Security.SecurityException>(() => SecureAndGenerate("WITH orders AS (SELECT * FROM orders) SELECT * FROM orders", options));

        // A schema-qualified reference is never the CTE: the physical table keeps its RLS
        string result = SecureAndGenerate("WITH orders AS (SELECT * FROM public.orders) SELECT * FROM orders", options);
        Assert.Contains("\"tenant_id\" = 42", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cte_QualifiedName_NeverTreatedAsCte()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42"),
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        // Even though CTE named 'orders' exists, schema.orders is qualified and must be treated as physical table
        string sql = "WITH orders AS (SELECT 1 AS x) SELECT * FROM public.orders";
        string result = SecureAndGenerate(sql, options);

        Assert.Contains("\"tenant_id\" = 42", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cte_OuterReference_UsesCteWithoutRls()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42"),
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        // Virtual CTE with no physical table references
        string sql = "WITH virtual_table AS (SELECT 1 AS col) SELECT * FROM virtual_table";
        string result = SecureAndGenerate(sql, options);

        // No physical table access, so tenant_id = 42 should not appear
        Assert.DoesNotContain("tenant_id = 42", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cte_SecC02_QuotedCteDoesNotShadowDifferentCasedPhysicalTable()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42", predicate: t => t == "mycte"),
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        // Quoted CTE "MyCte" must NOT shadow unquoted physical table 'mycte'
        string sql = "WITH \"MyCte\" AS (SELECT 1 AS col) SELECT * FROM mycte";
        string result = SecureAndGenerate(sql, options);

        // Physical table 'mycte' MUST have RLS applied
        Assert.Contains("\"tenant_id\" = 42", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cte_SecC02_QuotedReferenceDoesNotShadowLowercaseCte()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42", predicate: t => t == "MYCTE"),
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        // Unquoted CTE 'mycte' folds to lowercase; quoted "MYCTE" is case-sensitive and must not match the CTE
        string sql = "WITH mycte AS (SELECT 1 AS col) SELECT * FROM \"MYCTE\"";
        string result = SecureAndGenerate(sql, options);

        // Physical table "MYCTE" MUST have RLS applied
        Assert.Contains("\"tenant_id\" = 42", result, StringComparison.OrdinalIgnoreCase);
    }
}
