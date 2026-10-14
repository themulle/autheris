namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Linq;
using System.Security;
using Antlr4.Runtime.Misc;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Security;
using Xunit;
using static TrinoSqlEngine.Tests.Compiler.PolicyFixtures;

/// <summary>
/// Row-level security injection of the governed compile path (typed policy). Formerly these tests drove the visitor through the
/// removed string provider; the outcomes are asserted semantically now (bound values, placement, rejections).
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public sealed class AstSecurityVisitorRlsTests
{
    public static TheoryData<TargetSqlDialect> Dialects => new()
    {
        TargetSqlDialect.PostgreSql, TargetSqlDialect.SqlServer, TargetSqlDialect.Oracle
    };

    private static PolicyPredicate Parse(string text)
    {
        var parser = new PolicyExpressionParser(new FastSqlEngine());
        var context = Context(Orders);
        context.Catalog = Catalog();
        return parser.Parse(text, context.Build());
    }

    private static ProbeEnv WithPolicy(TargetSqlDialect dialect, string policy)
    {
        var env = new ProbeEnv(dialect);
        env.RowFilters.NoPolicy.Remove(Orders);
        env.RowFilters.Predicates[Orders] = Parse(policy);
        return env;
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Rls_SingleTable_InjectsTheTenantPredicate_WithABoundValue(TargetSqlDialect dialect)
    {
        var compiled = new ProbeEnv(dialect).Compile("SELECT id FROM orders");

        var tenant = Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.Tenant);
        Assert.Equal("acme", tenant.Value);
        Assert.DoesNotContain("acme", compiled.Sql);
        Assert.Contains("TenantId", compiled.Sql);
        Assert.NotEmpty(compiled.AppliedPredicates);
    }

    [Fact]
    public void Rls_CorrelatedExistsFilter_BindsTheTargetAlias()
    {
        var env = new ProbeEnv(TargetSqlDialect.SqlServer);
        env.PolicySubqueryPolicy();
        var compiled = env.Compile("SELECT id FROM orders");

        Assert.Contains(RowFilterAliases.Target, compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Entitlements", compiled.Sql);
        Assert.Contains("Orders", compiled.Sql);
    }

    [Theory]
    [InlineData("id IN (SELECT e.orderid FROM entitlements e WHERE e.orderid = autheris_target.id)", true)]
    [InlineData("id IN (SELECT e.orderid FROM entitlements e WHERE e.id > 0)", false)]
    public void Rls_InFilter_BindsTheTargetAliasOnlyWhenCorrelated_AndSecuresThePolicyTable(string policy, bool correlated)
    {
        var compiled = WithPolicy(TargetSqlDialect.SqlServer, policy).Compile("SELECT id FROM orders");

        Assert.Equal(correlated, compiled.Sql.Contains(RowFilterAliases.Target, StringComparison.OrdinalIgnoreCase));
        // the policy subquery reads a tenant table: it is tenant-restricted too (orders and entitlements)
        ProbeAsserts.TenantPredicates(compiled, 2);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Rls_SelectiveFiltering_OnlyTheTableWithAPolicyGetsThePolicyPredicate(TargetSqlDialect dialect)
    {
        var env = new ProbeEnv(dialect);
        env.RegionPolicy("EU");
        var compiled = env.Compile("SELECT o.id FROM orders o JOIN lookup l ON o.status = l.code");

        // one policy value for orders; lookup has neither a policy nor a tenant column
        Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.Policy && Equals(p.Value, "EU"));
        ProbeAsserts.TenantPredicates(compiled, 1);
    }

    [Fact]
    public void Rls_SqlServer_DerivedTableGetsAnAlias()
    {
        var compiled = new ProbeEnv(TargetSqlDialect.SqlServer).Compile("SELECT * FROM orders");

        // T-SQL requires derived tables to have an alias
        Assert.Contains("AS [orders]", compiled.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT id FROM \"Orders\"")]
    [InlineData("SELECT id FROM \"dbo\".\"Orders\"")]
    [InlineData("SELECT id FROM dbo.orders")]
    public void Rls_QuotedAndQualifiedTableNames_ResolveToTheCatalogTable_AndAreSecured(string sql)
    {
        var compiled = new ProbeEnv(TargetSqlDialect.PostgreSql).Compile(sql);

        Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.Tenant);
        Assert.Contains("TenantId", compiled.Sql);
    }

    [Fact]
    public void Rls_PolicyFilterWithAComment_IsRejected()
    {
        Assert.ThrowsAny<Exception>(() => Parse("region = 'EU' -- comment"));
        Assert.ThrowsAny<Exception>(() => Parse("region = 'EU' /* c */"));
    }

    [Fact]
    public void Rls_PolicyFilterWithADisallowedFunction_IsRejectedByTheFunctionPolicy()
    {
        Assert.ThrowsAny<SecurityException>(() => Parse("region = 'EU' AND dbms_pipe.receive_message('p', 1) = 0"));
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "\"o\"", "\"e\"", "\"l\"")]
    [InlineData(TargetSqlDialect.SqlServer, "[o]", "[e]", "[l]")]
    public void Rls_JoinWithExistsAndTableAliases_KeepsStructure_AndSecuresEveryTable(TargetSqlDialect dialect, string o, string e, string l)
    {
        string sql = @"
            SELECT o.id, o.amount, e.orderid
            FROM orders AS o
            JOIN entitlements AS e ON o.id = e.orderid
            WHERE EXISTS (
                SELECT 1
                FROM lookup AS l
                WHERE l.code = o.status AND o.region = 'DELIVERED'
            )";
        var compiled = new ProbeEnv(dialect).Compile(sql);

        // 1. aliases are preserved for correlation
        Assert.Contains($"AS {o}", compiled.Sql);
        Assert.Contains($"AS {e}", compiled.Sql);
        Assert.Contains($"AS {l}", compiled.Sql);

        // 2. join and EXISTS structure
        Assert.Contains("JOIN", compiled.Sql);
        Assert.Contains("EXISTS (", compiled.Sql);

        // 3. the literal is bound, never part of the text
        Assert.DoesNotContain("DELIVERED", compiled.Sql);
        Assert.Contains(compiled.Parameters, p => p.Origin == ParameterOrigin.QueryLiteral && Equals(p.Value, "DELIVERED"));

        // 4. both tenant tables carry the tenant predicate (the lookup table has no tenant column)
        ProbeAsserts.TenantPredicates(compiled, 2);
    }

    [Fact]
    public void TableQueryBody_IsDesugared_AndSecured()
    {
        var compiled = new ProbeEnv(TargetSqlDialect.PostgreSql).Compile("TABLE orders");

        Assert.Contains("SELECT", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.Tenant);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Rls_Predicate_IsInsideTheDerivedTable_SoAClientOrCannotEscapeIt(TargetSqlDialect dialect)
    {
        var compiled = new ProbeEnv(dialect).Compile("SELECT id FROM orders WHERE id = 1 OR id = 2");

        // the tenant marker is emitted before the client OR, which lives in the outer WHERE
        int lastTenantMarker = compiled.Parameters.Where(p => p.Origin == ParameterOrigin.Tenant)
            .Max(p => compiled.Sql.LastIndexOf(p.Marker, StringComparison.Ordinal));
        int clientOr = compiled.Sql.LastIndexOf(" OR ", StringComparison.Ordinal);
        Assert.True(lastTenantMarker < clientOr, compiled.Sql);
    }

    [Fact]
    public void ClientSql_WithBrackets_IsRejected_OnSqlServer()
    {
        Assert.ThrowsAny<ParseCanceledException>(() => new ProbeEnv(TargetSqlDialect.SqlServer).Compile("SELECT [Status] FROM orders"));
    }
}
