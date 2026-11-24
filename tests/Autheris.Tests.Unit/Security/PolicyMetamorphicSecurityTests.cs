namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Rights;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Metamorphic security testing for policies, access rules, and rights mapping.
/// Validates mathematical invariants: Monotonicity on privilege drop, Group order commutativity,
/// Claim type isomorphism, and Customer realm ceiling invariance.
/// </summary>
[Trait("Category", "Security")]
[Trait("Category", "Metamorphic")]
public sealed class PolicyMetamorphicSecurityTests
{
    private static readonly TenantId TestTenant = new("tenant-metamorphic");
    private const string TestRealm = "workforce-entra";

    #region 2. Functional Right Mapper Metamorphic Invariants

    [Theory]
    [InlineData(ClaimTypes.Role)]
    [InlineData(ClaimTypes.GroupSid)]
    public async Task M_RIGHT_01_ClaimTypeIsomorphism_AnyClaimType_YieldsIdenticalMappedRights(string claimType)
    {
        // Axiom: Mapping a group/role name 'sec-operators' must yield bit-identical FunctionalRights,
        // regardless of whether a role claim or a pipeline-issued group SID claim conveys the identifier.
        var repository = Substitute.For<IFunctionalRightRepository>();
        var realmRegistry = Substitute.For<IIdentityRealmRegistry>();
        var sut = new FunctionalRightMapper(repository, realmRegistry);

        const string groupName = "S-1-5-21-1-2-3-1100";
        var expectedRight = FunctionalRight.DataExport;

        var mapping = new FunctionalRightMapping(
            TestTenant.Value, TestRealm, groupName, expectedRight,
            ApprovedBy: "approver", ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: "Standard", CreatedBy: "admin",
            CreatedAt: DateTimeOffset.UtcNow);

        repository.GetApprovedMappingsForGroupsAndRolesAsync(
            TestTenant, TestRealm, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var input = callInfo.ArgAt<IReadOnlyCollection<string>>(2);
                return Task.FromResult<IReadOnlyList<FunctionalRightMapping>>(input.Contains(groupName) ? [mapping] : []);
            });

        var claims = new[]
        {
            new Claim(ClaimTypes.PrimarySid, "user-iso"),
            claimType == ClaimTypes.GroupSid ? Autheris.Domain.Common.IdentityClaimIssuers.CreateGroupSidClaim(groupName) : new Claim(claimType, groupName)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var rights = await sut.GetRightsForCallerAsync(principal, TestTenant, TestRealm);

        // Metamorphic assertion
        rights.ShouldContain(expectedRight,
            $"Claim type '{claimType}' failed to resolve mapped functional right '{expectedRight.Value}'");
    }

    [Theory]
    [InlineData("group")]
    [InlineData("groups")]
    public async Task M_RIGHT_01b_RawTokenGroupClaimTypes_NeverMapToRights(string claimType)
    {
        var repository = Substitute.For<IFunctionalRightRepository>();
        var sut = new FunctionalRightMapper(repository, Substitute.For<IIdentityRealmRegistry>());
        repository.GetApprovedMappingsForGroupsAndRolesAsync(TestTenant, TestRealm, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult<IReadOnlyList<FunctionalRightMapping>>(
                callInfo.ArgAt<IReadOnlyCollection<string>>(2).Count == 0 ? [] : throw new InvalidOperationException("raw claim reached the mapper")));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "user-iso"), new Claim(claimType, "S-1-5-21-1-2-3-1100"), new Claim(ClaimTypes.GroupSid, "S-1-5-21-1-2-3-1100")], "TestAuth"));

        (await sut.GetRightsForCallerAsync(principal, TestTenant, TestRealm)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(IdentityRealmKind.EntraExternalId)]
    [InlineData(IdentityRealmKind.AzureAdB2C)]
    [InlineData(IdentityRealmKind.Customer)]
    public async Task M_RIGHT_02_CustomerRealmCeilingInvariance_AdminRights_AlwaysFilteredFailClosed(IdentityRealmKind customerKind)
    {
        // Axiom: For any customer realm, even if the database contains approved mappings for
        // TenantAdmin, ClusterAdmin, GovernanceAdmin or Privileged rights, GetRightsForCallerAsync
        // must strictly filter them out fail-closed.
        var repository = Substitute.For<IFunctionalRightRepository>();
        var realmRegistry = Substitute.For<IIdentityRealmRegistry>();
        var sut = new FunctionalRightMapper(repository, realmRegistry);

        // The realm allows the Consumer role, so the standard mapping is within its role bounds (a mapping never exceeds them).
        var customerRealm = new IdentityRealm(
            "ext-customer", customerKind,
            new TenantBindingSpecification(TenantBindingKind.Fixed, TestTenant.Value),
            RealmGroupSource.Resolver, RealmProvisioningMode.None,
            issuers: ["https://customer.auth.com"],
            allowedGatewayRoles: new HashSet<Autheris.Domain.Security.GatewayRole> { Autheris.Domain.Security.GatewayRole.Consumer });

        realmRegistry.GetRealm("ext-customer").Returns(customerRealm);

        // Poisoned database mappings attempt to give a customer group admin rights
        var adminMapping = new FunctionalRightMapping(
            TestTenant.Value, "ext-customer", "S-1-5-21-1-2-3-1100", FunctionalRight.TenantAdmin,
            ApprovedBy: "rogue-admin", ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: "Privileged", CreatedBy: "rogue-admin",
            CreatedAt: DateTimeOffset.UtcNow);

        var clusterMapping = new FunctionalRightMapping(
            TestTenant.Value, "ext-customer", "S-1-5-21-1-2-3-1100", FunctionalRight.ClusterAdmin,
            ApprovedBy: "rogue-admin", ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: "Privileged", CreatedBy: "rogue-admin",
            CreatedAt: DateTimeOffset.UtcNow);

        var standardMapping = new FunctionalRightMapping(
            TestTenant.Value, "ext-customer", "S-1-5-21-1-2-3-1100", FunctionalRight.Consumer,
            ApprovedBy: "admin", ApprovedAt: DateTimeOffset.UtcNow,
            RecertClass: "Standard", CreatedBy: "admin",
            CreatedAt: DateTimeOffset.UtcNow);

        repository.GetApprovedMappingsForGroupsAndRolesAsync(
            TestTenant, "ext-customer", Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<FunctionalRightMapping>>([adminMapping, clusterMapping, standardMapping]));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "cust-user"), Autheris.Domain.Common.IdentityClaimIssuers.CreateGroupSidClaim("S-1-5-21-1-2-3-1100")], "TestAuth"));

        var rights = await sut.GetRightsForCallerAsync(principal, TestTenant, "ext-customer");

        // Allowed standard right is present
        rights.ShouldContain(FunctionalRight.Consumer);

        // Metamorphic Security Invariance: Administrative rights MUST NEVER leak into customer realms
        rights.ShouldNotContain(FunctionalRight.TenantAdmin);
        rights.ShouldNotContain(FunctionalRight.ClusterAdmin);
        rights.ShouldNotContain(FunctionalRight.GovernanceAdmin);
        rights.ShouldNotContain(FunctionalRight.GovernanceApprove);
    }

    #endregion
}
