namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// Security review: multi-valued claims never resolve to one of their values in the Casbin context (the last value used to win),
/// and empty claim values are treated as missing. Rules referencing such attributes fail closed.
/// </summary>
[Trait("Category", "Security")]
public sealed class CasbinAmbiguousAttributeTests
{
    private static readonly Sid UserSid = new("S-1-5-21-AMBIGUOUS-USER");
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TableIdentifier Table = new("finance", "dbo", "costs");

    private static SecurityEvaluationContext Context(IEnumerable<Claim> claims)
    {
        var (attributes, purpose) = TableAccessPolicy.BuildClaimAttributes(claims, extraAttributes: null);
        return new SecurityEvaluationContext(
            UserSid: UserSid,
            GroupSids: Array.Empty<Sid>(),
            Tenant: TenantA,
            TargetTable: Table,
            RequestedColumns: ["id"],
            ClientIp: IPAddress.Parse("10.1.2.3"),
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: purpose,
            Attributes: attributes);
    }

    // ---------------------------------------------------------------- attribute map

    [Fact]
    public void BuildClaimAttributes_DistinctValues_AreAmbiguous_NotLastWins()
    {
        var (attributes, _) = TableAccessPolicy.BuildClaimAttributes(
            [new Claim("department", "HR"), new Claim("department", "Finance")], null);

        attributes["department"].ShouldBeSameAs(CasbinEnforcementService.AmbiguousAttributeValue.Instance);
    }

    [Fact]
    public void BuildClaimAttributes_RepeatedSameValue_StaysSingleValue()
    {
        var (attributes, _) = TableAccessPolicy.BuildClaimAttributes(
            [new Claim("department", "HR"), new Claim("department", "HR")], null);

        attributes["department"].ShouldBe("HR");
    }

    [Fact]
    public void BuildClaimAttributes_ClaimTypesDifferingOnlyInCase_AreAmbiguous()
    {
        var (attributes, _) = TableAccessPolicy.BuildClaimAttributes(
            [new Claim("Department", "HR"), new Claim("department", "Finance")], null);

        attributes["department"].ShouldBeSameAs(CasbinEnforcementService.AmbiguousAttributeValue.Instance);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildClaimAttributes_EmptyValue_IsMissing(string value)
    {
        var (attributes, _) = TableAccessPolicy.BuildClaimAttributes([new Claim("cost_center", value)], null);

        attributes.ContainsKey("cost_center").ShouldBeFalse();
    }

    [Fact]
    public void BuildClaimAttributes_EmptyValueNextToRealValue_KeepsRealValue()
    {
        var (attributes, _) = TableAccessPolicy.BuildClaimAttributes(
            [new Claim("cost_center", " "), new Claim("cost_center", "FIN")], null);

        attributes["cost_center"].ShouldBe("FIN");
    }

    [Fact]
    public void BuildClaimAttributes_SeveralPurposes_GiveNoPurpose()
    {
        var (attributes, purpose) = TableAccessPolicy.BuildClaimAttributes(
            [new Claim("purpose", "AUDIT"), new Claim("purpose", "MARKETING")], null);

        purpose.ShouldBeNull();
        attributes["purpose"].ShouldBeSameAs(CasbinEnforcementService.AmbiguousAttributeValue.Instance);
    }

    [Fact]
    public void BuildClaimAttributes_EmptyPurpose_FallsBackToPurposeId()
    {
        var (_, purpose) = TableAccessPolicy.BuildClaimAttributes(
            [new Claim("purpose", ""), new Claim("purpose_id", "AUDIT")], null);

        purpose.ShouldBe("AUDIT");
    }

    [Fact]
    public void BuildClaimAttributes_ActionClaims_AreStillIgnored()
    {
        var (attributes, _) = TableAccessPolicy.BuildClaimAttributes(
            [new Claim("action", "write"), new Claim("gql.action", "write")], null);

        attributes.ContainsKey("action").ShouldBeFalse();
        attributes.ContainsKey("gql.action").ShouldBeFalse();
    }

    // ---------------------------------------------------------------- sub_rule evaluation

    [Fact]
    public async Task AllowRule_ReferencingAmbiguousAttribute_DoesNotMatch()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, UserSid.Value, Table.ToString(), "read", "r.ctx.Department == 'Finance'", "allow");

        var ambiguous = await casbin.EvaluatePolicyAsync(Context([new Claim("department", "HR"), new Claim("department", "Finance")]));
        ambiguous.IsAllowed.ShouldBeFalse();

        var single = await casbin.EvaluatePolicyAsync(Context([new Claim("department", "Finance")]));
        single.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task DenyRule_ReferencingAmbiguousAttribute_Matches()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, UserSid.Value, Table.ToString(), "read", "true", "allow");
        casbin.AddPolicy(TenantA, UserSid.Value, Table.ToString(), "read", "r.ctx.Department == 'Contractor'", "deny");

        // Previously the last value ("HR") won and the deny rule did not match.
        var ambiguous = await casbin.EvaluatePolicyAsync(Context([new Claim("department", "Contractor"), new Claim("department", "HR")]));
        ambiguous.IsAllowed.ShouldBeFalse();

        var employee = await casbin.EvaluatePolicyAsync(Context([new Claim("department", "HR")]));
        employee.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task DenyRule_ReferencingAmbiguousFallbackAttribute_Matches()
    {
        // Department falls back to 'dept' when 'department' is absent.
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, UserSid.Value, Table.ToString(), "read", "true", "allow");
        casbin.AddPolicy(TenantA, UserSid.Value, Table.ToString(), "read", "r.ctx.Department == 'Contractor'", "deny");

        var decision = await casbin.EvaluatePolicyAsync(Context([new Claim("dept", "Contractor"), new Claim("dept", "HR")]));

        decision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task DenyRule_ReferencingAmbiguousPurpose_Matches()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, UserSid.Value, Table.ToString(), "read", "true", "allow");
        casbin.AddPolicy(TenantA, UserSid.Value, Table.ToString(), "read", "r.ctx.PurposeId == 'MARKETING'", "deny");

        var decision = await casbin.EvaluatePolicyAsync(Context([new Claim("purpose", "AUDIT"), new Claim("purpose", "MARKETING")]));

        decision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task UnrelatedAmbiguousAttribute_DoesNotAffectOtherRules()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, UserSid.Value, Table.ToString(), "read", "r.ctx.Department == 'HR'", "allow", rlsFilter: "dept = '${department}'");

        var decision = await casbin.EvaluatePolicyAsync(Context(
            [new Claim("department", "HR"), new Claim("groups", "g1"), new Claim("groups", "g2")]));

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBe("dept = 'HR'");
    }

    // ---------------------------------------------------------------- ${attr.x} substitution

    [Fact]
    public async Task RowFilter_ReferencingAmbiguousAttribute_IsRejected()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, UserSid.Value, Table.ToString(), "read", "true", "allow", rlsFilter: "cost_center = ${attr.cost_center}");

        await Should.ThrowAsync<System.Security.SecurityException>(async () =>
            await casbin.EvaluatePolicyAsync(Context([new Claim("cost_center", "FIN"), new Claim("cost_center", "OPS")])));
    }

    [Fact]
    public async Task RowFilter_ReferencingAmbiguousDepartment_IsRejected()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, UserSid.Value, Table.ToString(), "read", "true", "allow", rlsFilter: "dept = '${department}'");

        await Should.ThrowAsync<System.Security.SecurityException>(async () =>
            await casbin.EvaluatePolicyAsync(Context([new Claim("department", "HR"), new Claim("department", "Finance")])));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RowFilter_EmptyAttributeValue_IsTreatedAsMissing(string value)
    {
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, UserSid.Value, Table.ToString(), "read", "true", "allow", rlsFilter: "cost_center = ${attr.cost_center}");

        // The claim path drops the empty value; a context built directly with an empty value is rejected as well.
        await Should.ThrowAsync<System.Security.SecurityException>(async () =>
            await casbin.EvaluatePolicyAsync(Context([new Claim("cost_center", value)])));

        var direct = Context([]) with { Attributes = new Dictionary<string, object?> { ["cost_center"] = value } };
        await Should.ThrowAsync<System.Security.SecurityException>(async () => await casbin.EvaluatePolicyAsync(direct));
    }

    [Fact]
    public async Task RowFilter_SingleValue_IsStillSubstituted()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.AddPolicy(TenantA, UserSid.Value, Table.ToString(), "read", "true", "allow", rlsFilter: "cost_center = ${attr.cost_center}");

        var decision = await casbin.EvaluatePolicyAsync(Context([new Claim("cost_center", "FIN"), new Claim("cost_center", "FIN")]));

        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBe("cost_center = 'FIN'");
    }
}
