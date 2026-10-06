namespace TrinoSqlEngine.Tests.Ast;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Generators;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Visitors;
using Xunit;

public sealed class AstSecurityVisitorRlsTests
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
    public void Rls_SingleTable_InjectsSubqueryWithTenantFilter()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42"),
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        string sql = "SELECT id FROM orders";
        string result = SecureAndGenerate(sql, options);

        Assert.Contains("WHERE \"tenant_id\" = 42", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orders", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rls_CorrelatedFilter_BindsAutherisTargetAlias()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("EXISTS (SELECT 1 FROM tenant_dep d WHERE d.dep_id = autheris_target.dep_id)"),
            TargetDialect = TargetSqlDialect.SqlServer
        };

        string sql = "SELECT id FROM orders";
        string result = SecureAndGenerate(sql, options);

        // Target table must be aliased as autheris_target inside subquery
        Assert.Contains(RowFilterAliases.Target, result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orders", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rls_PolicyProvider_SelectiveFiltering()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider(
                "tenant_id = 99",
                predicate: tableName => tableName.Equals("orders", StringComparison.OrdinalIgnoreCase)),
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        string sql = "SELECT * FROM orders JOIN public_info ON orders.id = public_info.id";
        string result = SecureAndGenerate(sql, options);

        // orders should have RLS subquery
        Assert.Contains("\"tenant_id\" = 99", result, StringComparison.OrdinalIgnoreCase);
        // public_info should remain direct table source without RLS filter
        Assert.DoesNotContain("public_info WHERE", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rls_SqlServer_EnsuresSubqueryTableAlias()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 1"),
            TargetDialect = TargetSqlDialect.SqlServer
        };

        string sql = "SELECT * FROM orders";
        string result = SecureAndGenerate(sql, options);

        // T-SQL requires derived tables to have an alias, e.g. AS [orders]
        Assert.Contains("AS [orders]", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rls_QuotedTableName_AppliesPolicyCorrectly()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 77", predicate: t => t == "orders"),
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        // Quoted table "orders" must match policy for 'orders'
        string sql = "SELECT * FROM \"orders\"";
        string result = SecureAndGenerate(sql, options);

        Assert.Contains("\"tenant_id\" = 77", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rls_QuotedQualifiedTableName_AppliesPolicyCorrectly()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 88", predicate: t => t == "public.orders"),
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        // Quoted qualified table "public"."orders" must match policy for 'public.orders'
        string sql = "SELECT * FROM \"public\".\"orders\"";
        string result = SecureAndGenerate(sql, options);

        Assert.Contains("\"tenant_id\" = 88", result, StringComparison.OrdinalIgnoreCase);
    }
}
