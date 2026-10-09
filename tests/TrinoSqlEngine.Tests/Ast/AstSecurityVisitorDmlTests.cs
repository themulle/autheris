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

public sealed class AstSecurityVisitorDmlTests
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
    public void Dml_UnfilteredDml_ThrowsUnfilteredDmlException()
    {
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            RejectUnfilteredDml = true
        };

        Assert.Throws<UnfilteredDmlException>(() => SecureAndGenerate("DELETE FROM orders", options));
        Assert.Throws<UnfilteredDmlException>(() => SecureAndGenerate("UPDATE orders SET x = 1", options));
    }

    [Fact]
    public void Dml_TautologyWhere_ThrowsUnfilteredDmlException()
    {
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            RejectUnfilteredDml = true
        };

        Assert.Throws<UnfilteredDmlException>(() => SecureAndGenerate("DELETE FROM orders WHERE 1 = 1", options));
        Assert.Throws<UnfilteredDmlException>(() => SecureAndGenerate("DELETE FROM orders WHERE 'a' = 'a'", options));
        Assert.Throws<UnfilteredDmlException>(() => SecureAndGenerate("UPDATE orders SET x = 1 WHERE true", options));
        Assert.Throws<UnfilteredDmlException>(() => SecureAndGenerate("DELETE FROM orders WHERE 1 < 2", options));
        Assert.Throws<UnfilteredDmlException>(() => SecureAndGenerate("UPDATE orders SET x = 1 WHERE 10 > 5", options));
        Assert.Throws<UnfilteredDmlException>(() => SecureAndGenerate("DELETE FROM orders WHERE 5 <= 5", options));
        Assert.Throws<UnfilteredDmlException>(() => SecureAndGenerate("UPDATE orders SET x = 1 WHERE 5 >= 2", options));
        Assert.Throws<UnfilteredDmlException>(() => SecureAndGenerate("DELETE FROM orders WHERE 1 != 2", options));
    }

    [Fact]
    public void Dml_WhereWithoutColumnReference_ThrowsUnfilteredDmlException()
    {
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            RejectUnfilteredDml = true
        };

        Assert.Throws<UnfilteredDmlException>(() => SecureAndGenerate("UPDATE orders SET x = 1 WHERE 'a' != 'b'", options));
        Assert.Throws<UnfilteredDmlException>(() => SecureAndGenerate("DELETE FROM orders WHERE 100 + 200 = 300", options));
    }

    [Fact]
    public void Dml_LegitimateColumnFilter_Passes()
    {
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            RejectUnfilteredDml = true
        };

        var deleteResult = SecureAndGenerate("DELETE FROM orders WHERE id = 1", options);
        Assert.Contains("DELETE FROM", deleteResult, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orders", deleteResult, StringComparison.OrdinalIgnoreCase);

        var updateResult = SecureAndGenerate("UPDATE orders SET x = 1 WHERE amount > 100", options);
        Assert.Contains("UPDATE", updateResult, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orders", updateResult, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dml_MaskedColumnsInDml_ThrowsSecurityException()
    {
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            RejectMaskedColumnsInDml = true,
            ColumnMaskingProvider = new DefaultColumnMaskingPolicyProvider(
                (t, c) => c.Equals("salary", StringComparison.OrdinalIgnoreCase),
                (t, c) => "NULL")
        };

        // Probing masked column in WHERE is rejected
        Assert.Throws<SecurityException>(() => SecureAndGenerate("DELETE FROM employees WHERE salary > 50000", options));

        // Assigning masked column in SET is rejected
        Assert.Throws<SecurityException>(() => SecureAndGenerate("UPDATE employees SET salary = 100000 WHERE id = 1", options));
    }

    [Fact]
    public void Dml_ConsentFilteredInsert_ThrowsSecurityException()
    {
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            RejectConsentFilteredInsert = true,
            TablesWithConsentRowFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "patients" }
        };

        string sql = "INSERT INTO patients (id, name, tenant_id) VALUES (1, 'Alice', 42)";
        Assert.Throws<SecurityException>(() => SecureAndGenerate(sql, options));
    }

    [Fact]
    public void Dml_WithCheckOption_InsertTenantValidation()
    {
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            EnforceWithCheckOption = true,
            TenantColumnName = "tenant_id",
            ExpectedTenantValue = "42",
            RequireTenantColumnInInsert = true
        };

        // Valid insert
        string validSql = "INSERT INTO orders (id, amount, tenant_id) VALUES (1, 100, 42)";
        string res = SecureAndGenerate(validSql, options);
        Assert.Contains("INSERT INTO", res, StringComparison.OrdinalIgnoreCase);

        // Missing tenant column
        string missingTenantSql = "INSERT INTO orders (id, amount) VALUES (1, 100)";
        Assert.Throws<SecurityException>(() => SecureAndGenerate(missingTenantSql, options));

        // Wrong tenant value
        string wrongTenantSql = "INSERT INTO orders (id, amount, tenant_id) VALUES (1, 100, 99)";
        Assert.Throws<SecurityException>(() => SecureAndGenerate(wrongTenantSql, options));
    }

    [Fact]
    public void Dml_WithCheckOption_UpdateTenantModification_ThrowsSecurityException()
    {
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            EnforceWithCheckOption = true,
            TenantColumnName = "tenant_id",
            ExpectedTenantValue = "42",
            DisallowTenantColumnModificationInUpdate = true
        };

        string sql = "UPDATE orders SET tenant_id = 42 WHERE id = 1";
        Assert.Throws<SecurityException>(() => SecureAndGenerate(sql, options));
    }

    [Fact]
    public void Dml_SubqueryInUpdateSetAndWhere_RewritesWithRls()
    {
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 't1'"),
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        string sql = "UPDATE orders SET total = (SELECT sum(price) FROM items) WHERE customer_id IN (SELECT id FROM customers)";
        string result = SecureAndGenerate(sql, options);

        // All three tables (orders, items, customers) must have RLS applied
        Assert.Contains("WHERE \"tenant_id\" = 't1'", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("items", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("customers", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dml_SubqueryInDeleteWhere_RewritesWithRls()
    {
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 't1'"),
            TargetDialect = TargetSqlDialect.PostgreSql
        };

        string sql = "DELETE FROM orders WHERE customer_id IN (SELECT id FROM customers)";
        string result = SecureAndGenerate(sql, options);

        Assert.Contains("WHERE \"tenant_id\" = 't1'", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("customers", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dml_MaskedColumnInCastOrLike_ThrowsSecurityException()
    {
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            RejectMaskedColumnsInDml = true,
            ColumnMaskingProvider = new DefaultColumnMaskingPolicyProvider(
                (t, c) => c.Equals("salary", StringComparison.OrdinalIgnoreCase),
                (t, c) => "NULL")
        };

        // CAST on masked column in WHERE is rejected
        Assert.Throws<SecurityException>(() => SecureAndGenerate("DELETE FROM employees WHERE CAST(salary AS VARCHAR) = '50000'", options));

        // LIKE on masked column in WHERE is rejected
        Assert.Throws<SecurityException>(() => SecureAndGenerate("DELETE FROM employees WHERE salary LIKE '500%'", options));

        // Subquery referencing masked column in UPDATE SET is rejected
        Assert.Throws<SecurityException>(() => SecureAndGenerate("UPDATE notes SET content = (SELECT salary FROM employees WHERE id = 1) WHERE id = 1", options));
    }

    [Fact]
    public void Dml_TautologyWhere_ColumnComparison_ThrowsUnfilteredDmlException()
    {
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            RejectUnfilteredDml = true
        };

        Assert.Throws<UnfilteredDmlException>(() => SecureAndGenerate("DELETE FROM orders WHERE id <= id", options));
        Assert.Throws<UnfilteredDmlException>(() => SecureAndGenerate("UPDATE orders SET x = 1 WHERE id >= id", options));
        Assert.Throws<UnfilteredDmlException>(() => SecureAndGenerate("DELETE FROM orders WHERE id BETWEEN id AND id", options));
        Assert.Throws<UnfilteredDmlException>(() => SecureAndGenerate("DELETE FROM orders WHERE id IN (id)", options));
    }

    [Fact]
    public void Dml_ExistsWithoutColumnReference_ThrowsUnfilteredDmlException()
    {
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            RejectUnfilteredDml = true
        };

        // Subquery without any column reference does not satisfy column reference requirement
        Assert.Throws<UnfilteredDmlException>(() => SecureAndGenerate("DELETE FROM orders WHERE EXISTS (SELECT 1)", options));
    }

    [Fact]
    public void Dml_MaskedColumnInJoinOnOrGroupByOrEscape_ThrowsSecurityException()
    {
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            RejectMaskedColumnsInDml = true,
            ColumnMaskingProvider = new DefaultColumnMaskingPolicyProvider(
                (t, c) => c.Equals("salary", StringComparison.OrdinalIgnoreCase),
                (t, c) => "NULL")
        };

        // JOIN ON referencing masked column in WHERE subquery
        Assert.Throws<SecurityException>(() => SecureAndGenerate("DELETE FROM orders WHERE id IN (SELECT o.id FROM orders o JOIN employees e ON o.user_id = e.salary)", options));

        // GROUP BY referencing masked column in WHERE subquery
        Assert.Throws<SecurityException>(() => SecureAndGenerate("DELETE FROM orders WHERE id IN (SELECT count(*) FROM employees GROUP BY salary)", options));

        // LIKE ESCAPE referencing masked column in WHERE
        Assert.Throws<SecurityException>(() => SecureAndGenerate("DELETE FROM employees WHERE name LIKE 'test%' ESCAPE salary", options));
    }
}

