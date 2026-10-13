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

    // The legacy RlsListener / RewriteRls rewriting tests are replaced by the governed compile path (FastSqlEngine.Compile). Their
    // outcomes are asserted by these tests and by Compiler/TypedInjectionTests, CteShadowingTests, DmlCoverageTests,
    // DmlCompileTests (DML mechanics, tenant forcing, guards) and CompileApiTests (read-only, DDL and table functions rejected).

    [Fact]
    public void Compile_SimpleSelect_WrapsTheTableAndBindsTheTenant()
    {
        var compiled = new TrinoSqlEngine.Tests.Security.ProbeEnv(TargetSqlDialect.PostgreSql)
            .Compile("SELECT id, amount FROM orders WHERE amount > 100");

        Assert.Contains(compiled.Parameters, p => p.Origin == TrinoSqlEngine.Ast.Emit.ParameterOrigin.Tenant);
        Assert.Contains("SELECT", compiled.Sql);
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
    public void Compile_AllowlistedTableFunction_IsStillRejected_FailClosed()
    {
        // SEC H-14: the governed compiler has no emission rule for polymorphic table functions; an allowlist cannot open them.
        var env = new TrinoSqlEngine.Tests.Security.ProbeEnv(TargetSqlDialect.SqlServer);
        var request = env.Request() with { AllowedTableFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "my_ptf" } };

        var ex = Record.Exception(() =>
            env.Engine.Compile("SELECT * FROM TABLE(my_ptf(TABLE(orders)))".AsMemory(), request, System.Threading.CancellationToken.None));
        Assert.True(ex is System.Security.SecurityException or NotSupportedException, ex?.GetType().Name ?? "compiled");
    }

    [Theory]
    [InlineData("UPDATE orders SET status = 'shipped', tenantid = 'other' WHERE id = 10")]
    [InlineData("UPDATE orders SET status = 'shipped', \"TenantId\" = 'other' WHERE id = 10")]
    public void Compile_UpdateOfTheTenantColumn_IsRejected(string sql)
    {
        Assert.ThrowsAny<System.Security.SecurityException>(() =>
            new TrinoSqlEngine.Tests.Security.ProbeEnv(TargetSqlDialect.SqlServer).Compile(sql));
    }

    [Theory]
    [InlineData("INSERT INTO orders (id, tenantid) VALUES (1, 'other')")]
    [InlineData("INSERT INTO orders (id, \"TenantId\") VALUES (1, 'acme'), (2, 'other')")]
    [InlineData("INSERT INTO orders (id, tenantid) SELECT 1, 'other'")]
    public void Compile_InsertWithAForeignTenantValue_IsRejected(string sql)
    {
        Assert.ThrowsAny<System.Security.SecurityException>(() =>
            new TrinoSqlEngine.Tests.Security.ProbeEnv(TargetSqlDialect.SqlServer).Compile(sql));
    }
}
