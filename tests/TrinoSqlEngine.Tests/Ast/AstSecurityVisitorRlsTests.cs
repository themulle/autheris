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

    [Fact]
    public void Rls_InjectedFilterWithComments_IsRejectedWhenRejectCommentsEnabled()
    {
        var options = new RlsOptions
        {
            RejectComments = true,
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42 -- comment"),
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        string sql = "SELECT id FROM orders";
        Assert.Throws<Antlr4.Runtime.Misc.ParseCanceledException>(() => SecureAndGenerate(sql, options));
    }

    [Fact]
    public void Rls_InjectedFilterWithDisallowedFunction_IsRejectedByFunctionPolicy()
    {
        var options = new RlsOptions
        {
            EnforceFunctionPolicy = true,
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42 AND dbms_pipe.receive_message('p', 1) = 0"),
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        string sql = "SELECT id FROM orders";
        Assert.Throws<System.Security.SecurityException>(() => SecureAndGenerate(sql, options));
    }

    [Fact]
    public void Rls_JoinWithExistsAndTableAliases_GeneratesSecuredDialectSql_PostgreSql()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 't1'"),
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        string sql = @"
            SELECT o.id, o.total, c.name 
            FROM orders AS o 
            JOIN customers AS c ON o.customer_id = c.id 
            WHERE EXISTS (
                SELECT 1 
                FROM shipments AS s 
                WHERE s.order_id = o.id AND s.status = 'DELIVERED'
            )";

        string result = _engine.GenerateGovernedSql(sql, options);

        // 1. All three physical tables must be wrapped with RLS subqueries
        Assert.Contains("\"orders\"", result);
        Assert.Contains("\"customers\"", result);
        Assert.Contains("\"shipments\"", result);

        // 2. Table aliases must be preserved for correlation
        Assert.Contains("AS \"o\"", result);
        Assert.Contains("AS \"c\"", result);
        Assert.Contains("AS \"s\"", result);

        // 3. JOIN condition and EXISTS subquery structure must be preserved
        Assert.Contains("JOIN", result);
        Assert.Contains("\"o\".\"customer_id\" = \"c\".\"id\"", result);
        Assert.Contains("EXISTS (", result);
        Assert.Contains("\"s\".\"order_id\" = \"o\".\"id\"", result);
        Assert.Contains("'DELIVERED'", result);

        // 4. RLS tenant filter must be injected
        Assert.Contains("\"tenant_id\" = 't1'", result);
    }

    [Fact]
    public void Rls_JoinWithExistsAndTableAliases_GeneratesSecuredDialectSql_SqlServer()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 't1'"),
            TargetDialect = TargetSqlDialect.SqlServer
        };

        string sql = @"
            SELECT o.id, o.total, c.name 
            FROM orders AS o 
            JOIN customers AS c ON o.customer_id = c.id 
            WHERE EXISTS (
                SELECT 1 
                FROM shipments AS s 
                WHERE s.order_id = o.id AND s.status = 'DELIVERED'
            )";

        string result = _engine.GenerateGovernedSql(sql, options);

        // 1. Square brackets for SQL Server identifiers
        Assert.Contains("[orders]", result);
        Assert.Contains("[customers]", result);
        Assert.Contains("[shipments]", result);

        // 2. Table aliases preserved with T-SQL brackets
        Assert.Contains("AS [o]", result);
        Assert.Contains("AS [c]", result);
        Assert.Contains("AS [s]", result);

        // 3. Predicate preservation
        Assert.Contains("[o].[customer_id] = [c].[id]", result);
        Assert.Contains("EXISTS (", result);
        Assert.Contains("[s].[order_id] = [o].[id]", result);
        Assert.Contains("N'DELIVERED'", result);
        Assert.Contains("[tenant_id] = N't1'", result);
    }

    [Fact]
    public void TableQueryBody_RewritesWithRlsAndMasking()
    {
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 't1'"),
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        string sql = "TABLE orders";
        string result = SecureAndGenerate(sql, options);

        // Desugared and rewritten with RLS subquery
        Assert.Contains("SELECT", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orders", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE \"tenant_id\" = 't1'", result, StringComparison.OrdinalIgnoreCase);
    }
}
