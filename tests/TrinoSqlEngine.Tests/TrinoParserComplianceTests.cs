namespace TrinoSqlEngine;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using Xunit;

public class TrinoParserComplianceTests
{
    public static IEnumerable<object[]> GetTrinoStatements()
    {
        string jsonPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "trino_statements.json");
        if (!File.Exists(jsonPath))
        {
            jsonPath = Path.Combine(Directory.GetCurrentDirectory(), "Fixtures", "trino_statements.json");
        }

        if (!File.Exists(jsonPath))
            yield break;

        var queries = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(jsonPath))!;
        foreach (var query in queries)
        {
            yield return new object[] { query };
        }
    }

    public static IEnumerable<object[]> GetTrinoExpressions()
    {
        string jsonPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "trino_expressions.json");
        if (!File.Exists(jsonPath))
        {
            jsonPath = Path.Combine(Directory.GetCurrentDirectory(), "Fixtures", "trino_expressions.json");
        }

        if (!File.Exists(jsonPath))
            yield break;

        var queries = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(jsonPath))!;
        foreach (var query in queries)
        {
            yield return new object[] { query };
        }
    }

    public static IEnumerable<object[]> GetTrinoInvalidStatements()
    {
        string jsonPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "trino_statements_invalid.json");
        if (!File.Exists(jsonPath))
        {
            jsonPath = Path.Combine(Directory.GetCurrentDirectory(), "Fixtures", "trino_statements_invalid.json");
        }

        if (!File.Exists(jsonPath))
            yield break;

        var queries = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(jsonPath))!;
        foreach (var query in queries)
        {
            yield return new object[] { query };
        }
    }

    public static IEnumerable<object[]> GetTrinoInvalidExpressions()
    {
        string jsonPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "trino_expressions_invalid.json");
        if (!File.Exists(jsonPath))
        {
            jsonPath = Path.Combine(Directory.GetCurrentDirectory(), "Fixtures", "trino_expressions_invalid.json");
        }

        if (!File.Exists(jsonPath))
            yield break;

        var queries = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(jsonPath))!;
        foreach (var query in queries)
        {
            yield return new object[] { query };
        }
    }

    [Theory]
    [MemberData(nameof(GetTrinoStatements))]
    public void Statement_ShouldParseWithoutError(string sql)
    {
        var exception = Record.Exception(() =>
        {
            var engine = new FastSqlEngine();
            var (tree, tokens) = engine.Parse(sql.AsMemory());

            Assert.NotNull(tree);
            Assert.NotNull(tokens);
        });

        Assert.Null(exception);
    }

    [Theory]
    [MemberData(nameof(GetTrinoExpressions))]
    public void Expression_ShouldParseWithoutError(string sql)
    {
        var exception = Record.Exception(() =>
        {
            var engine = new FastSqlEngine();
            var (tree, tokens) = engine.ParseExpression(sql.AsMemory());

            Assert.NotNull(tree);
            Assert.NotNull(tokens);
        });

        Assert.Null(exception);
    }

    [Theory]
    [MemberData(nameof(GetTrinoInvalidStatements))]
    public void Statement_InvalidShouldFail(string sql)
    {
        var engine = new FastSqlEngine();
        Assert.ThrowsAny<ParseCanceledException>(() =>
        {
            engine.Parse(sql.AsMemory());
        });
    }

    [Fact]
    public void Rls_ShouldRewriteSimpleSelect()
    {
        var engine = new FastSqlEngine();
        var query = "SELECT id, amount FROM orders WHERE amount > 100".AsMemory();

        var (tree, tokens) = engine.Parse(query);

        var rewriter = new RlsListener(tokens);
        ParseTreeWalker.Default.Walk(rewriter, tree);

        string securedQuery = rewriter.GetSecuredSql();
        Assert.Equal("SELECT id, amount FROM (SELECT * FROM orders WHERE tenant_id = 42) WHERE amount > 100", securedQuery);
    }

    [Fact]
    public void Rls_ShouldNotRewriteCteTable()
    {
        var engine = new FastSqlEngine();
        var query = "WITH orders AS (SELECT 1 AS id) SELECT * FROM orders".AsMemory();

        var (tree, tokens) = engine.Parse(query);

        var rewriter = new RlsListener(tokens);
        ParseTreeWalker.Default.Walk(rewriter, tree);

        string securedQuery = rewriter.GetSecuredSql();
        Assert.Equal("WITH orders AS (SELECT 1 AS id) SELECT * FROM orders", securedQuery);
    }

    [Fact]
    public void Rls_ShouldRejectDmlWhenReadOnlyEnforced()
    {
        var engine = new FastSqlEngine();
        var dmlQuery = "DELETE FROM orders WHERE id = 1".AsMemory();

        Assert.Throws<System.Security.SecurityException>(() =>
        {
            engine.RewriteRls(dmlQuery, new RlsOptions { EnforceReadOnlyQueries = true });
        });
    }

    [Fact]
    public void Rls_ShouldAppendAliasWhenOptionEnabled()
    {
        var engine = new FastSqlEngine();
        var query = "SELECT orders.id FROM orders".AsMemory();

        string secured = engine.RewriteRls(query, new RlsOptions { AppendTableAlias = true });
        Assert.Equal("SELECT orders.id FROM (SELECT * FROM orders WHERE tenant_id = 42) AS orders", secured);
    }

    [Fact]
    public void Rls_ShouldUseCustomPolicyProvider()
    {
        var engine = new FastSqlEngine();
        var query = "SELECT * FROM orders JOIN audit_log ON orders.id = audit_log.order_id".AsMemory();

        var customPolicy = new DefaultRlsPolicyProvider(
            defaultFilter: "org_id = 'ABC'",
            predicate: table => !table.Equals("audit_log", StringComparison.OrdinalIgnoreCase));

        string secured = engine.RewriteRls(query, new RlsOptions { PolicyProvider = customPolicy });
        Assert.Contains("WHERE org_id = 'ABC'", secured);
        // audit_log is excluded from policy filter
        Assert.Contains("JOIN audit_log ON", secured);
    }

    [Fact]
    public void Engine_ShouldEnforceMaxQueryLength()
    {
        var engine = new FastSqlEngine { MaxQueryLength = 100 };
        string hugeSql = "SELECT " + new string('x', 200);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            engine.Parse(hugeSql.AsMemory());
        });
    }

    [Fact]
    public void Engine_RewriteRlsFacadeMethodWorks()
    {
        var engine = new FastSqlEngine();
        var query = "SELECT id, amount FROM orders WHERE amount > 100".AsMemory();

        string secured = engine.RewriteRls(query);
        Assert.Equal("SELECT id, amount FROM (SELECT * FROM orders WHERE tenant_id = 42) WHERE amount > 100", secured);
    }

    [Fact]
    public void Rls_ShouldHandleQuotedIdentifiersAndNormalization()
    {
        var engine = new FastSqlEngine();
        var options = new RlsOptions
        {
            PolicyProvider = new DefaultRlsPolicyProvider("tenant_id = 42", t => t.Equals("orders", StringComparison.OrdinalIgnoreCase))
        };

        // Quoted table: "orders"
        string sql1 = "SELECT * FROM \"orders\"";
        string secured1 = engine.RewriteRls(sql1.AsMemory(), options);
        Assert.Equal("SELECT * FROM (SELECT * FROM \"orders\" WHERE tenant_id = 42)", secured1);

        // Schema qualified: "my_schema"."orders"
        string sql2 = "SELECT * FROM \"my_schema\".\"orders\"";
        string secured2 = engine.RewriteRls(sql2.AsMemory(), options);
        Assert.Equal("SELECT * FROM (SELECT * FROM \"my_schema\".\"orders\" WHERE tenant_id = 42)", secured2);
    }

    [Fact]
    public void Rls_ShouldPreventCteShadowingAttack()
    {
        var engine = new FastSqlEngine();
        // Attack: Attacker tries to shadow 'orders' with CTE named 'orders', hoping the inner query bypasses RLS
        string sql = "WITH orders AS (SELECT * FROM orders) SELECT * FROM orders";
        string secured = engine.RewriteRls(sql.AsMemory());

        // Inner physical 'orders' must be secured, while outer CTE reference is in CTE scope
        Assert.Equal("WITH orders AS (SELECT * FROM (SELECT * FROM orders WHERE tenant_id = 42)) SELECT * FROM orders", secured);
    }

    [Fact]
    public void Rls_ShouldSupportMultiCteChaining()
    {
        var engine = new FastSqlEngine();
        string sql = "WITH a AS (SELECT * FROM orders), b AS (SELECT * FROM a) SELECT * FROM b";
        string secured = engine.RewriteRls(sql.AsMemory());

        Assert.Equal("WITH a AS (SELECT * FROM (SELECT * FROM orders WHERE tenant_id = 42)), b AS (SELECT * FROM a) SELECT * FROM b", secured);
    }

    [Fact]
    public void Rls_ShouldRewritePolymorphicTableFunctionArguments()
    {
        var engine = new FastSqlEngine();
        string sql = "SELECT * FROM TABLE(my_ptf(TABLE(orders)))";
        // SEC H-14: table functions are rejected unless allowlisted.
        var options = new RlsOptions { AllowedTableFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "my_ptf" } };
        string secured = engine.RewriteRls(sql.AsMemory(), options);

        Assert.Equal("SELECT * FROM TABLE(my_ptf(TABLE((SELECT * FROM orders WHERE tenant_id = 42))))", secured);
    }

    [Fact]
    public void Rls_DeleteWithoutWhere_AppendsFilter()
    {
        var engine = new FastSqlEngine();
        // Tests the RLS WHERE injection mechanics; the unfiltered-DML guardrail is covered by DML_* tests.
        var options = new RlsOptions { EnforceReadOnlyQueries = false, RejectUnfilteredDml = false };
        string sql = "DELETE FROM orders";
        string secured = engine.RewriteRls(sql.AsMemory(), options);

        Assert.Equal("DELETE FROM orders WHERE (tenant_id = 42)", secured);
    }

    [Fact]
    public void Rls_DeleteWithWhere_InjectsAndWrapsInParentheses()
    {
        var engine = new FastSqlEngine();
        // Tests the RLS WHERE injection mechanics; the unfiltered-DML guardrail is covered by DML_* tests.
        var options = new RlsOptions { EnforceReadOnlyQueries = false, RejectUnfilteredDml = false };
        string sql = "DELETE FROM orders WHERE id = 10 OR 1=1";
        string secured = engine.RewriteRls(sql.AsMemory(), options);

        // Parentheses prevent OR bypass
        Assert.Equal("DELETE FROM orders WHERE (tenant_id = 42) AND (id = 10 OR 1=1)", secured);
    }

    [Fact]
    public void Rls_UpdateWithoutWhere_AppendsFilter()
    {
        var engine = new FastSqlEngine();
        // Tests the RLS WHERE injection mechanics; the unfiltered-DML guardrail is covered by DML_* tests.
        var options = new RlsOptions { EnforceReadOnlyQueries = false, RejectUnfilteredDml = false };
        string sql = "UPDATE orders SET status = 'shipped'";
        string secured = engine.RewriteRls(sql.AsMemory(), options);

        Assert.Equal("UPDATE orders SET status = 'shipped' WHERE (tenant_id = 42)", secured);
    }

    [Fact]
    public void Rls_UpdateWithWhere_InjectsAndWrapsInParentheses()
    {
        var engine = new FastSqlEngine();
        var options = new RlsOptions { EnforceReadOnlyQueries = false };
        string sql = "UPDATE orders SET status = 'shipped' WHERE id = 10";
        string secured = engine.RewriteRls(sql.AsMemory(), options);

        Assert.Equal("UPDATE orders SET status = 'shipped' WHERE (tenant_id = 42) AND (id = 10)", secured);
    }

    [Fact]
    public void Rls_UpdateWithCheckOption_RejectsTenantModification()
    {
        var engine = new FastSqlEngine();
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            EnforceWithCheckOption = true,
            DisallowTenantColumnModificationInUpdate = true
        };

        string sqlUnquoted = "UPDATE orders SET status = 'shipped', tenant_id = 99 WHERE id = 10";
        Assert.Throws<System.Security.SecurityException>(() =>
        {
            engine.RewriteRls(sqlUnquoted.AsMemory(), options);
        });

        string sqlQuoted = "UPDATE orders SET status = 'shipped', \"tenant_id\" = 99 WHERE id = 10";
        Assert.Throws<System.Security.SecurityException>(() =>
        {
            engine.RewriteRls(sqlQuoted.AsMemory(), options);
        });
    }

    [Fact]
    public void Rls_InsertWithCheckOption_AcceptsMatchingTenant()
    {
        var engine = new FastSqlEngine();
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            EnforceWithCheckOption = true,
            ExpectedTenantValue = "42"
        };

        string sql = "INSERT INTO orders (id, tenant_id) VALUES (1, 42)";
        string secured = engine.RewriteRls(sql.AsMemory(), options);
        Assert.Equal("INSERT INTO orders (id, tenant_id) VALUES (1, 42)", secured);
    }

    [Fact]
    public void Rls_InsertWithCheckOption_RejectsMismatchingTenant()
    {
        var engine = new FastSqlEngine();
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            EnforceWithCheckOption = true,
            ExpectedTenantValue = "42"
        };

        string sqlSingle = "INSERT INTO orders (id, tenant_id) VALUES (1, 99)";
        Assert.Throws<System.Security.SecurityException>(() =>
        {
            engine.RewriteRls(sqlSingle.AsMemory(), options);
        });

        string sqlMulti = "INSERT INTO orders (id, \"tenant_id\") VALUES (1, 42), (2, 99)";
        Assert.Throws<System.Security.SecurityException>(() =>
        {
            engine.RewriteRls(sqlMulti.AsMemory(), options);
        });
    }

    [Fact]
    public void Rls_InsertSelectWithCheckOption_RejectsConstantMismatch()
    {
        var engine = new FastSqlEngine();
        var options = new RlsOptions
        {
            EnforceReadOnlyQueries = false,
            EnforceWithCheckOption = true,
            ExpectedTenantValue = "42"
        };

        string sql = "INSERT INTO orders (id, tenant_id) SELECT 1, 99";
        Assert.Throws<System.Security.SecurityException>(() =>
        {
            engine.RewriteRls(sql.AsMemory(), options);
        });
    }

    [Fact]
    public void Rls_RejectsDdlAndAdministrativeCommands()
    {
        var engine = new FastSqlEngine();
        var options = new RlsOptions { EnforceReadOnlyQueries = false };

        Assert.Throws<System.Security.SecurityException>(() =>
        {
            engine.RewriteRls("DROP TABLE orders".AsMemory(), options);
        });

        Assert.Throws<System.Security.SecurityException>(() =>
        {
            engine.RewriteRls("CREATE TABLE orders (id INT)".AsMemory(), options);
        });

        Assert.Throws<System.Security.SecurityException>(() =>
        {
            engine.RewriteRls("TRUNCATE TABLE orders".AsMemory(), options);
        });
    }
}
