namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Governance.Rights;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Identity;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>PERS-09: functional-right claims come only from the FunctionalRightMapper (single authority); a mapper failure issues none.</summary>
[Trait("Category", "Security")]
public sealed class EnterpriseClaimsTransformationFunctionalRightTests
{
    private static IIdentityRealmRegistry Registry()
    {
        var registry = Substitute.For<IIdentityRealmRegistry>();
        var realm = new IdentityRealm(
            "resolver-realm",
            IdentityRealmKind.EntraWorkforce,
            new TenantBindingSpecification(TenantBindingKind.Fixed, FixedTenantId: new TenantId("tenant-a")),
            groupSource: RealmGroupSource.Resolver);
        registry.GetDefaultRealm().Returns(realm);
        registry.GetRealm("resolver-realm").Returns(realm); // DT-A-3: the tenant resolver looks the stamped realm up by id
        return registry;
    }

    private static IRoleResolver Resolver()
    {
        var resolver = Substitute.For<IRoleResolver>();
        resolver.ResolveRolesAsync(Arg.Any<ResolvedIdentity>(), Arg.Any<CancellationToken>())
            .Returns(new RoleResolution(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SecGroup" }, new HashSet<string>(),
                new Dictionary<string, string>(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "resolved"));
        return resolver;
    }

    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity([new Claim("oid", "user-1"), new Claim("tenant_id", "tenant-a")], "TestAuth"));

    [Fact]
    public async Task ClaimsTransformation_OnlyMapperApprovedRights_BecomeClaims()
    {
        var mapper = Substitute.For<IFunctionalRightMapper>();
        mapper.GetRightsForGroupsAndRolesAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<FunctionalRight>>(new HashSet<FunctionalRight> { FunctionalRight.DataExport }));
        var transformation = new EnterpriseClaimsTransformation(Registry(), Resolver(), mapper);

        var result = await transformation.TransformAsync(Principal());

        var rights = result.FindAll(RequireFunctionalRightHandler.FunctionalRightClaimType).Select(c => c.Value).ToList();
        rights.ShouldBe([FunctionalRight.DataExport.Value]);
    }

    [Fact]
    public async Task ClaimsTransformation_SelfApprovedPrivilegedMapping_NoClaim()
    {
        // The mapper is the authority: it filters a self-approved privileged mapping and returns nothing for it.
        var mapper = Substitute.For<IFunctionalRightMapper>();
        mapper.GetRightsForGroupsAndRolesAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<FunctionalRight>>(new HashSet<FunctionalRight>()));
        var transformation = new EnterpriseClaimsTransformation(Registry(), Resolver(), mapper);

        var result = await transformation.TransformAsync(Principal());

        result.FindAll(RequireFunctionalRightHandler.FunctionalRightClaimType).ShouldBeEmpty();
    }

    [Fact]
    public async Task ClaimsTransformation_MapperFails_IssuesNoFunctionalRightClaims()
    {
        var mapper = Substitute.For<IFunctionalRightMapper>();
        mapper.GetRightsForGroupsAndRolesAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlySet<FunctionalRight>>>(_ => throw new InvalidOperationException("store down"));
        var transformation = new EnterpriseClaimsTransformation(Registry(), Resolver(), mapper);

        var result = await transformation.TransformAsync(Principal());

        result.FindAll(RequireFunctionalRightHandler.FunctionalRightClaimType).ShouldBeEmpty();
    }
}
