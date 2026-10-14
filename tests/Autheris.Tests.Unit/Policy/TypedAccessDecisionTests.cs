using System.Collections.Generic;
using System.Security;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Policy;

/// <summary>WP-X0e: the typed row policy on <see cref="TableAccessDecision"/> (SEC-ADG-02 item 3, SEC-ADG-25).</summary>
public sealed class TypedAccessDecisionTests
{
    private static readonly TableIdentifier Orders = new("d", "main", "orders");
    private static readonly PolicyPredicateRenderer Renderer = new();
    private static readonly TableMetadata Meta = new() { Identifier = Orders };

    private static IRowPolicy Policy(string column, string prefix, string value, TargetSqlDialect dialect = TargetSqlDialect.PostgreSql)
    {
        var namer = new PolicyParameterNamer(prefix);
        var parameter = namer.Add(new PolicyValue(value, SqlParameterType.String));
        var expression = new TrinoSqlEngine.Ast.Nodes.BinaryExpression(
            PolicyParameterNamer.Column(column), TrinoSqlEngine.Ast.Nodes.BinaryOperator.Equal, parameter);
        return new PolicyPredicateRowPolicy(PolicyPredicate.Create(expression, namer.Values), dialect, Renderer);
    }

    private static TableAccessDecision Allowed(IRowPolicy? policy = null)
    {
        var decision = TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        return policy is null ? decision : decision.WithAdditionalRowPolicy(policy);
    }

    [Fact]
    public void Restrict_TypedAnd_ConflictDenies()
    {
        var consent = Allowed(Policy("region", "p_", "EU"));
        var abac = Allowed(Policy("region", "p_", "US"));

        var result = TableAccessPolicy.Restrict(consent, abac, Meta);

        result.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Restrict_TypedAnd_MergesBothPoliciesIntoOne()
    {
        var consent = Allowed(Policy("region", "c_", "EU"));
        var abac = Allowed(Policy("status", "a_", "OPEN"));

        var result = TableAccessPolicy.Restrict(consent, abac, Meta);

        result.IsAllowed.ShouldBeTrue();
        result.RowPolicy.ShouldNotBeNull();
        var text = result.RowPolicy!.Project();
        text.Sql.ShouldContain("region");
        text.Sql.ShouldContain("status");
        text.Sql.ShouldNotContain("EU");
        text.Sql.ShouldNotContain("OPEN");
        text.Parameters.Values.ShouldBe(["EU", "OPEN"], ignoreOrder: true);
        result.CombinedRowFilterSql.ShouldBe(text.Sql);
        result.RowFilterParameters.ShouldNotBeNull();
        result.RowFilterParameters!.Values.ShouldBe(["EU", "OPEN"], ignoreOrder: true);
        result.RequireTypedRowPolicy().IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void CombinedRowFilterSql_IsDerivedFromRowPolicy()
    {
        var policy = Policy("region", "p_", "EU");

        var decision = Allowed(policy);

        decision.RowPolicy.ShouldBeSameAs(policy);
        decision.CombinedRowFilterSql.ShouldBe(policy.Project().Sql);
        decision.CombinedRowFilterSql.ShouldNotBeNullOrWhiteSpace();
        decision.RowFilterParameters.ShouldBe(policy.Project().Parameters);
    }

    [Fact]
    public void StringOnlyDecision_DeniedOnCompilerPath()
    {
        var decision = TableAccessDecision.Allowed(Orders, new Dictionary<string, ColumnAccessLevel>(), rowFilterSql: "region = 'EU'");

        var result = decision.RequireTypedRowPolicy();

        result.IsAllowed.ShouldBeFalse();
        result.DeniedReasons.ShouldNotBeEmpty();
    }

    [Fact]
    public void DriftedString_DeniedOnCompilerPath()
    {
        var decision = Allowed(Policy("region", "p_", "EU")) with { CombinedRowFilterSql = "1 = 1" };

        decision.RequireTypedRowPolicy().IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void LegacyStringAppendedToTypedDecision_DeniedOnCompilerPath()
    {
        var decision = Allowed(Policy("region", "p_", "EU")).WithAdditionalRowFilter("status = 'OPEN'");

        decision.RequireTypedRowPolicy().IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void DecisionWithoutRowFilter_PassesCompilerPath()
    {
        var decision = Allowed();

        decision.RequireTypedRowPolicy().ShouldBeSameAs(decision);
    }

    [Fact]
    public void TypedDecision_PassesCompilerPath()
    {
        var decision = Allowed(Policy("region", "p_", "EU"));

        decision.RequireTypedRowPolicy().ShouldBeSameAs(decision);
    }

    [Fact]
    public void DeniedDecision_StaysDeniedOnCompilerPath()
    {
        TableAccessDecision.Denied(Orders, "no").RequireTypedRowPolicy().IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void DenyAll_AndAnything_IsDenyAll()
    {
        var deny = new PolicyPredicateRowPolicy(PolicyPredicate.DenyAll, TargetSqlDialect.PostgreSql, Renderer);
        var other = Policy("region", "p_", "EU");

        deny.IsDenyAll.ShouldBeTrue();
        other.IsDenyAll.ShouldBeFalse();
        deny.And(other).IsDenyAll.ShouldBeTrue();
        other.And(deny).IsDenyAll.ShouldBeTrue();
    }

    [Fact]
    public void And_ConflictingParameter_ThrowsSecurityException()
    {
        Should.Throw<SecurityException>(() => Policy("region", "p_", "EU").And(Policy("region", "p_", "US")));
    }

    [Fact]
    public void And_DifferentDialects_ThrowsSecurityException()
    {
        Should.Throw<SecurityException>(() =>
            Policy("region", "a_", "EU", TargetSqlDialect.PostgreSql).And(Policy("status", "b_", "x", TargetSqlDialect.SqlServer)));
    }

    [Fact]
    public void Fingerprint_IgnoresValues_ChangesWithShape()
    {
        Policy("region", "p_", "EU").Fingerprint.ShouldBe(Policy("region", "p_", "US").Fingerprint);
        Policy("region", "p_", "EU").Fingerprint.ShouldNotBe(Policy("status", "p_", "EU").Fingerprint);
        Policy("region", "p_", "EU").Fingerprint.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void VirtualFilter_Typed_RoundTripsThroughEnforcementRemoval()
    {
        var consent = Policy("region", "c_", "EU");
        var vf = Policy("owner", "v_", "me");
        var binding = new AppliedVirtualFilterBinding(
            Autheris.Tests.Unit.VirtualFilters.MandatoryRowFilterResolverTests.Filter("vf1", "owner_id"),
            new FilterBinding { FilterName = "vf1", TargetPattern = "d.main.orders" },
            1);
        var decision = Allowed(consent).WithMandatoryPredicate(vf, ["vf1"], [binding]);

        decision.RowPolicy!.Project().Sql.ShouldContain("owner");
        decision.MandatoryRowPredicateSql.ShouldNotBeNullOrWhiteSpace();
        decision.RequireTypedRowPolicy().IsAllowed.ShouldBeTrue();

        var residual = decision.WithoutEnforcedVirtualFilters([binding]);

        residual.RowPolicy!.Fingerprint.ShouldBe(consent.Fingerprint);
        residual.CombinedRowFilterSql.ShouldBe(consent.Project().Sql);
        residual.MandatoryRowPredicateSql.ShouldBeNull();
        residual.RequireTypedRowPolicy().IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void TenantPredicate_IsBoundParameter()
    {
        var factory = new RowPolicyFactory(Renderer);

        var policy = factory.ForTenant("tenant_id", new Autheris.Domain.Common.TenantId("acme"), TargetSqlDialect.PostgreSql);

        var text = policy.Project();
        text.Sql.ShouldContain("tenant_id");
        text.Sql.ShouldNotContain("acme");
        text.Parameters.Values.ShouldContain("acme");
    }
}
