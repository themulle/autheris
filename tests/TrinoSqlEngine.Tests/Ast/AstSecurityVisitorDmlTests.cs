namespace TrinoSqlEngine.Tests.Ast;

using System;
using System.Linq;
using System.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Tests.Security;
using Xunit;

/// <summary>
/// DML guardrails of the governed compile path (typed policy), ported from the string-path visitor tests. Covered elsewhere and
/// therefore not repeated here: consent-filtered INSERT (<c>AstSecurityRegressionTests.SQ07_*</c>), the WITH CHECK OPTION for INSERT
/// and UPDATE (<c>DmlCompileTests.InsertValues_*</c>, <c>Update_TenantColumnAssignment_IsRejected</c>), subqueries in UPDATE/DELETE
/// (<c>Update_SubqueriesInWhereAndSet_AreSecuredOnEverySource</c>, <c>Delete_SubqueryInWhere_IsSecured</c>).
/// </summary>
[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public sealed class AstSecurityVisitorDmlTests
{
    public static TheoryData<TargetSqlDialect> Dialects => new()
    {
        TargetSqlDialect.PostgreSql, TargetSqlDialect.SqlServer, TargetSqlDialect.Oracle
    };

    private static ProbeEnv Env(TargetSqlDialect dialect, bool maskEmail = false)
    {
        var env = new ProbeEnv(dialect);
        if (maskEmail) env.MaskEmail();
        return env;
    }

    private static ProbeEnv MaskedEnv(string kind)
    {
        var env = new ProbeEnv(TargetSqlDialect.PostgreSql);
        if (kind == "hmac") env.MaskEmailHmac(); else env.MaskEmailPartial();
        return env;
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Dml_UnfilteredDml_ThrowsUnfilteredDmlException(TargetSqlDialect dialect)
    {
        var env = Env(dialect);
        Assert.Throws<UnfilteredDmlException>(() => env.Compile("DELETE FROM orders"));
        Assert.Throws<UnfilteredDmlException>(() => env.Compile("UPDATE orders SET status = 'x'"));
    }

    [Theory]
    [InlineData("'a' = 'a'")]
    [InlineData("1 < 2")]
    [InlineData("10 > 5")]
    [InlineData("5 <= 5")]
    [InlineData("5 >= 2")]
    [InlineData("1 != 2")]
    [InlineData("'a' != 'b'")]
    [InlineData("100 + 200 = 300")]
    [InlineData("1 = 1")]
    [InlineData("true")]
    [InlineData("id <= id")]
    [InlineData("id >= id")]
    [InlineData("id BETWEEN id AND id")]
    [InlineData("id IN (id)")]
    [InlineData("EXISTS (SELECT 1)")]
    public void Dml_TautologyOrColumnFreeWhere_ThrowsUnfilteredDmlException(string where)
    {
        var env = Env(TargetSqlDialect.PostgreSql);
        Assert.Throws<UnfilteredDmlException>(() => env.Compile("DELETE FROM orders WHERE " + where));
        Assert.Throws<UnfilteredDmlException>(() => env.Compile("UPDATE orders SET status = 'x' WHERE " + where));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Dml_LegitimateColumnFilter_Compiles_AndCarriesTheTenant(TargetSqlDialect dialect)
    {
        var env = Env(dialect);

        var delete = env.Compile("DELETE FROM orders WHERE id = 1");
        Assert.Equal(SqlStatementClass.Delete, delete.StatementClass);
        Assert.Single(delete.Parameters, p => p.Origin == ParameterOrigin.Tenant);

        var update = env.Compile("UPDATE orders SET status = 'x' WHERE amount > 100");
        Assert.Equal(SqlStatementClass.Update, update.StatementClass);
        Assert.Single(update.Parameters, p => p.Origin == ParameterOrigin.Tenant);
    }

    [Theory]
    [InlineData("DELETE FROM orders WHERE email = 'x'")]
    [InlineData("UPDATE orders SET email = 'x' WHERE id = 1")]
    [InlineData("DELETE FROM orders WHERE CAST(email AS VARCHAR) = 'x'")]
    [InlineData("DELETE FROM orders WHERE email LIKE 'x%'")]
    [InlineData("DELETE FROM orders WHERE status LIKE 'test%' ESCAPE email")]
    [InlineData("UPDATE entitlements SET orderid = (SELECT id FROM orders WHERE email = 'x') WHERE id = 1")]
    [InlineData("DELETE FROM entitlements WHERE orderid IN (SELECT o.id FROM orders o JOIN entitlements e ON o.id = e.orderid WHERE o.email = 'x')")]
    public void Dml_MaskedColumnInWhereSetLikeCastSubqueryOrGroupBy_ThrowsSecurityException(string sql)
    {
        Assert.ThrowsAny<SecurityException>(() => Env(TargetSqlDialect.PostgreSql, maskEmail: true).Compile(sql));
    }

    // GROUP BY over a masked column inside a DML subquery: the typed path only ever groups the masked projection, but the
    // aggregate guard (WP-FEAT-02) rejects it anyway to close the cardinality inference channel.
    [Fact]
    public void Dml_GroupByAMaskedColumnInASubquery_IsRejected()
    {
        // WP-FEAT-02: grouping by a masked column is rejected outright (even though only the mask would be grouped),
        // because the group cardinality of a keyed or partial mask is an inference channel.
        Assert.Throws<MaskedAggregationException>(() => Env(TargetSqlDialect.PostgreSql, maskEmail: true)
            .Compile("DELETE FROM entitlements WHERE orderid IN (SELECT count(*) FROM orders GROUP BY email)"));
    }

    [Theory]
    [InlineData("hmac")]
    [InlineData("partial")]
    public void Dml_GroupByAMaskedColumnInASubquery_Hmac_And_PartialMask_AreRejected(string kind)
    {
        Assert.Throws<MaskedAggregationException>(() => MaskedEnv(kind)
            .Compile("DELETE FROM entitlements WHERE orderid IN (SELECT count(*) FROM orders GROUP BY email)"));
    }

    [Theory]
    [InlineData("UPDATE orders SET status = (SELECT sub.val FROM (SELECT orders.*) sub) WHERE id = 1")]
    [InlineData("UPDATE orders SET status = 'x' WHERE (SELECT count(*) FROM (SELECT orders.*) sub) > 0")]
    [InlineData("DELETE FROM orders WHERE (SELECT count(*) FROM (SELECT orders.*) sub) > 0")]
    [InlineData("DELETE FROM dbo.orders WHERE (SELECT count(*) FROM (SELECT dbo.orders.*) sub) > 0")]
    public void Sg06_TargetWildcardInSubquery_ThrowsSecurityException_WhenTheTargetHasMaskedColumns(string sql)
    {
        var ex = Assert.ThrowsAny<SecurityException>(() => Env(TargetSqlDialect.PostgreSql, maskEmail: true).Compile(sql));
        Assert.Contains("Whole-row reference", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Sg05_UserWhere_IsParenthesized_AndCombinedWithTheInjectedPredicates(TargetSqlDialect dialect)
    {
        var env = Env(dialect);
        env.RegionPolicy();

        var delete = env.Compile("DELETE FROM orders WHERE id = 1 OR status = 'x'");
        var update = env.Compile("UPDATE orders SET status = 'done' WHERE id = 1 OR status = 'x'");

        foreach (var compiled in new[] { delete, update })
        {
            // the user OR can never absorb the injected tenant and policy predicates
            Assert.Contains(") AND (", compiled.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.Tenant);
            Assert.Single(compiled.Parameters, p => p.Origin == ParameterOrigin.Policy);
        }
    }
}
