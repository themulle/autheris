namespace Autheris.Tests.Unit.Databricks.Provisioning;

using Autheris.Application.Governance.Provisioning;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Databricks.Provisioning.UcProvisioningScenario;

/// <summary>Issue #1122: pure planning steps of the reconciler.</summary>
[Trait("Category", "Security")]
public sealed class UcProvisioningPlannerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-27T10:00:00Z");

    [Theory]
    [InlineData("8B0C4B5E-6F0E-4A63-9A5E-1B2A4F9D7C11", "8b0c4b5e-6f0e-4a63-9a5e-1b2a4f9d7c11")]
    [InlineData("{8b0c4b5e-6f0e-4a63-9a5e-1b2a4f9d7c11}", "8b0c4b5e-6f0e-4a63-9a5e-1b2a4f9d7c11")]
    [InlineData("S-1-5-21-1", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void External_ids_are_normalized_guids_only(string? raw, string? expected)
    {
        UcProvisioningPlanner.NormalizeExternalId(raw).ShouldBe(expected);
    }

    [Fact]
    public void Two_consents_of_one_group_on_one_table_want_the_same_two_privileges()
    {
        var first = Grant(Now);
        var second = Grant(Now);
        var target = UcEligibility.Provisionable(new UcTablePrivilege("main", "sales", "orders", UcPrivileges.Select));
        var resolutions = new Dictionary<(GranteeType, string), UcPrincipalResolution> { [(GranteeType.Group, GroupOid)] = Group("analysts") };

        var desired = UcProvisioningPlanner.BuildDesired([(first, target), (second, target)], resolutions);

        desired.Privileges.Count.ShouldBe(2);
        desired.Privileges.Values.ShouldAllBe(p => p.ConsentIds.Count == 2 && p.Principal == "analysts");
        desired.Consents.ShouldAllBe(p => p.IsProvisionable && p.Keys.Count == 2);
    }

    [Fact]
    public void Unsupported_and_unresolved_consents_want_nothing()
    {
        var unsupported = Grant(Now);
        var unresolved = Grant(Now, grantee: OtherOid);
        var target = UcEligibility.Provisionable(new UcTablePrivilege("main", "sales", "orders", UcPrivileges.Select));
        var resolutions = new Dictionary<(GranteeType, string), UcPrincipalResolution> { [(GranteeType.Group, OtherOid)] = UcPrincipalResolution.Ambiguous };

        var desired = UcProvisioningPlanner.BuildDesired([(unsupported, UcEligibility.Unsupported(UcReasonCodes.RowFilters)), (unresolved, target)], resolutions);

        desired.Privileges.ShouldBeEmpty();
        desired.Consents.Select(p => p.UnsupportedReason).ShouldBe([UcReasonCodes.RowFilters, UcReasonCodes.PrincipalAmbiguous]);
    }

    [Fact]
    public void Deny_is_found_on_the_table_or_through_a_matching_or_unparsable_pattern()
    {
        var table = TableId();
        UcProvisioningPlanner.HasDeny(table, [Grant(Now)], []).ShouldBeFalse();
        UcProvisioningPlanner.HasDeny(table, [Grant(Now, effect: ConsentEffect.Deny)], []).ShouldBeTrue();
        UcProvisioningPlanner.HasDeny(table, [Grant(Now, table: "customers", effect: ConsentEffect.Deny)], []).ShouldBeFalse();
        UcProvisioningPlanner.HasDeny(table, [], [new Consent { ObjectPattern = "dbx.sales.ord*", Effect = ConsentEffect.Deny }]).ShouldBeTrue();
        UcProvisioningPlanner.HasDeny(table, [], [new Consent { ObjectPattern = "dbx.finance.*", Effect = ConsentEffect.Deny }]).ShouldBeFalse();
        UcProvisioningPlanner.HasDeny(table, [], [new Consent { ObjectPattern = "broken", Effect = ConsentEffect.Deny }]).ShouldBeTrue();
        UcProvisioningPlanner.HasDeny(table, [], [new Consent { ObjectPattern = "dbx.sales.*", Effect = ConsentEffect.Allow }]).ShouldBeFalse();
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(4, 240)]
    [InlineData(20, 300)]
    public void Backoff_doubles_and_is_capped(int attempts, int seconds)
    {
        UcProvisioningPlanner.Backoff(attempts, TimeSpan.FromMinutes(5)).ShouldBe(TimeSpan.FromSeconds(seconds));
    }
}
