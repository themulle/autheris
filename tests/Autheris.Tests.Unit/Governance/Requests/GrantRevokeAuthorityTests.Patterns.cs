using System;
using System.Threading.Tasks;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Requests;

/// <summary>Pattern grants, catalog grants (SEC-1009-36) and the verified authority handed to the repository (SEC-1009-43).</summary>
[Trait("Category", "Security")]
public sealed partial class GrantRevokeAuthorityTests
{
    private static Consent PatternGrantTo(string granteeSid) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TenantA,
        TableIdentifier = default,
        ObjectPattern = "sales.dbo.*",
        DataSource = "sales",
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(granteeSid),
        ValidFrom = DateTimeOffset.UnixEpoch,
        ValidTo = DateTimeOffset.UnixEpoch.AddYears(80)
    };

    private static CatalogVisibilityGrant CatalogGrantTo(string granteeSid, TenantId? tenant = null) => new()
    {
        TenantId = tenant ?? TenantA,
        ContainerKind = ContainerKind.Domain,
        ContainerId = DomainId,
        GranteeSid = new Sid(granteeSid),
        ValidFrom = DateTimeOffset.UnixEpoch
    };

    [Fact]
    public async Task Pattern_grant_without_owner_is_not_revocable_by_it()
    {
        // The pattern target (source, "", "") resolves to "no owner"; that must not open the IT route.
        var authority = Authority(ownerGroup: null);

        (await authority.EvaluateAsync(ContextFor("user:it", "TenantAdmin"), PatternGrantTo("user:alice"))).ShouldBeNull();
        (await authority.EvaluateAsync(ContextFor("user:owner", groups: [OwnerGroupSid]), PatternGrantTo("user:alice"))).ShouldBeNull();
    }

    [Fact]
    public async Task Pattern_grant_is_revocable_by_governance_and_grantee_only()
    {
        var authority = Authority(OwnerGroupSid);

        (await authority.EvaluateAsync(ContextFor("user:dg", "GovernanceAdmin"), PatternGrantTo("user:alice"))).ShouldBe(GrantRevokeCapacity.Governance);
        (await authority.EvaluateAsync(ContextFor("user:alice"), PatternGrantTo("user:alice"))).ShouldBe(GrantRevokeCapacity.Grantee);
        (await authority.EvaluateAsync(ContextFor("user:owner", groups: [OwnerGroupSid]), PatternGrantTo("user:alice"))).ShouldBeNull();
    }

    [Fact]
    public async Task Verified_revocation_carries_the_capacity_and_is_bound_to_the_grant_tenant()
    {
        var authority = Authority(OwnerGroupSid);
        var grant = GrantTo("user:alice");

        var verified = await authority.AuthorizeAsync(ContextFor("user:owner", groups: [OwnerGroupSid]), grant);

        verified.ShouldNotBeNull();
        verified.Capacity.ShouldBe(GrantRevokeCapacity.ContainerOwner);
        verified.Authority.VerifiedCapacity.ShouldBe(nameof(GrantRevokeCapacity.ContainerOwner));
        verified.Authority.CanAccessTenant(TenantA).ShouldBeTrue();
        verified.Authority.CanAccessTenant(TenantB).ShouldBeFalse();
        (await authority.AuthorizeAsync(ContextFor("user:mallory"), grant)).ShouldBeNull();
    }

    [Fact]
    public async Task Catalog_grant_is_revocable_by_owner_member_governance_and_it()
    {
        var authority = Authority(OwnerGroupSid);
        var grant = CatalogGrantTo("user:alice");

        (await authority.EvaluateCatalogAsync(ContextFor("user:owner", groups: [OwnerGroupSid]), grant)).ShouldBe(GrantRevokeCapacity.ContainerOwner);
        (await authority.EvaluateCatalogAsync(ContextFor("user:dg", "GovernanceAdmin"), grant)).ShouldBe(GrantRevokeCapacity.Governance);
        (await authority.EvaluateCatalogAsync(ContextFor("user:it", "TenantAdmin"), grant)).ShouldBe(GrantRevokeCapacity.ItAdmin);
        (await authority.EvaluateCatalogAsync(ContextFor("user:mallory"), grant)).ShouldBeNull();
    }

    [Fact]
    public async Task Catalog_grant_revoke_fails_closed_on_degraded_groups_and_foreign_tenant()
    {
        var authority = Authority(OwnerGroupSid);

        (await authority.EvaluateCatalogAsync(
            ContextFor("user:owner", groups: [OwnerGroupSid], groupStatus: GroupResolutionStatus.Degraded), CatalogGrantTo("user:alice"))).ShouldBeNull();
        (await authority.EvaluateCatalogAsync(
            ContextFor("user:dg", "GovernanceAdmin", groupStatus: GroupResolutionStatus.Degraded), CatalogGrantTo("user:alice"))).ShouldBeNull();
        (await authority.EvaluateCatalogAsync(
            ContextFor("user:dg", "GovernanceAdmin", tenant: TenantB), CatalogGrantTo("user:alice"))).ShouldBeNull();
    }
}
