namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Identity;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>GOV-08: an unresolvable user key or a degraded role resolution marks the request; no shared cache entry for an empty key.</summary>
[Trait("Category", "Security")]
public sealed class EnterpriseClaimsTransformationDegradedTests
{
    private static IdentityRealm ResolverRealm() => new(
        "resolver-realm",
        IdentityRealmKind.EntraWorkforce,
        new TenantBindingSpecification(TenantBindingKind.Fixed, FixedTenantId: new TenantId("tenant-a")),
        groupSource: RealmGroupSource.Resolver);

    private static IIdentityRealmRegistry Registry()
    {
        var registry = Substitute.For<IIdentityRealmRegistry>();
        registry.GetDefaultRealm().Returns(ResolverRealm());
        registry.GetRealm("resolver-realm").Returns(ResolverRealm()); // DT-A-3: the tenant resolver looks the stamped realm up by id
        return registry;
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "TestAuth"));

    [Fact]
    public async Task EmptyUserKey_MarksDegraded_NoSharedCacheEntry()
    {
        var resolver = Substitute.For<IRoleResolver>();
        var transformation = new EnterpriseClaimsTransformation(Registry(), resolver);

        var result = await transformation.TransformAsync(Principal(new Claim("tenant_id", "tenant-a")));

        result.HasClaim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded).ShouldBeTrue();
        await resolver.DidNotReceiveWithAnyArgs().ResolveRolesAsync(default!, default);
    }

    [Fact]
    public async Task DegradedResolution_AddsDegradedClaim()
    {
        var resolver = Substitute.For<IRoleResolver>();
        resolver.ResolveRolesAsync(Arg.Any<ResolvedIdentity>(), Arg.Any<CancellationToken>())
            .Returns(new RoleResolution(
                new HashSet<string>(), new HashSet<string>(), new Dictionary<string, string>(),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "degraded")
            { Status = RoleResolutionStatus.Degraded });
        var transformation = new EnterpriseClaimsTransformation(Registry(), resolver);

        var result = await transformation.TransformAsync(Principal(new Claim("oid", "user-1"), new Claim("tenant_id", "tenant-a")));

        result.HasClaim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded).ShouldBeTrue();
    }

    [Fact]
    public async Task ResolvedResolution_DoesNotAddDegradedClaim()
    {
        var resolver = Substitute.For<IRoleResolver>();
        resolver.ResolveRolesAsync(Arg.Any<ResolvedIdentity>(), Arg.Any<CancellationToken>())
            .Returns(new RoleResolution(
                new HashSet<string>(), new HashSet<string>(), new Dictionary<string, string>(),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "resolved"));
        var transformation = new EnterpriseClaimsTransformation(Registry(), resolver);

        var result = await transformation.TransformAsync(Principal(new Claim("oid", "user-1"), new Claim("tenant_id", "tenant-a")));

        result.Claims.Any(c => c.Type == RoleResolutionClaims.ClaimType).ShouldBeFalse();
    }
}
