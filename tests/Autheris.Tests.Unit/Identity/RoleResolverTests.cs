namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class RoleResolverTests
{
    private readonly IIdentityRealmRegistry _realmRegistry = Substitute.For<IIdentityRealmRegistry>();
    private readonly IRoleResolverCache _cache = Substitute.For<IRoleResolverCache>();
    private readonly IRoleProvider _provider = Substitute.For<IRoleProvider>();

    [Fact]
    public async Task RoleResolver_NamespacesExternalGroups_AndStampsRealm()
    {
        var realmId = "customer-b2b";
        var realm = new IdentityRealm(
            realmId,
            IdentityRealmKind.Customer,
            new TenantBindingSpecification(TenantBindingKind.Fixed, "tenant-42"),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.Scim,
            issuers: ["https://customer.auth.com"]);

        _realmRegistry.GetRealm(realmId).Returns(realm);
        _provider.ProviderId.Returns("scim-store");

        var identity = new ResolvedIdentity(
            "user-1",
            "user1@customer.com",
            "User One",
            "user1@customer.com",
            new TenantId("tenant-42"),
            realmId,
            new List<Claim>()
        );

        _provider.GetRolesAsync(identity, realm, Arg.Any<CancellationToken>())
            .Returns(new ProviderRoleResult(new HashSet<string> { "billing-admins" }, new HashSet<string> { "Viewer" }, new Dictionary<string, string>()));

        var sut = new RoleResolver(_realmRegistry, [_provider], _cache);

        var resolution = await sut.ResolveRolesAsync(identity);

        // Group must be namespaced with realm prefix
        resolution.Groups.ShouldContain("customer-b2b|billing-admins");

        // Customer realm must NOT include the bare un-namespaced group
        resolution.Groups.ShouldNotContain("billing-admins");
    }

    [Fact]
    public async Task RoleResolver_WorkforceRealm_PreservesLegacyGroupAndNamespaced()
    {
        var realmId = "workforce-entra";
        var realm = new IdentityRealm(
            realmId,
            IdentityRealmKind.EntraWorkforce,
            new TenantBindingSpecification(TenantBindingKind.Fixed, new TenantId("tenant-corp")),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.Scim,
            issuers: ["https://login.microsoftonline.com/corp"]);

        _realmRegistry.GetRealm(realmId).Returns(realm);
        _provider.ProviderId.Returns("scim-store");

        var identity = new ResolvedIdentity(
            "user-corp",
            "corp@company.com",
            "Corp User",
            "corp@company.com",
            new TenantId("tenant-corp"),
            realmId,
            new List<Claim>()
        );

        _provider.GetRolesAsync(identity, realm, Arg.Any<CancellationToken>())
            .Returns(new ProviderRoleResult(new HashSet<string> { "S-1-5-21-12345" }, new HashSet<string> { "Admin" }, new Dictionary<string, string>()));

        var sut = new RoleResolver(_realmRegistry, [_provider], _cache);

        var resolution = await sut.ResolveRolesAsync(identity);

        // Both namespaced and un-namespaced legacy SID must be present
        resolution.Groups.ShouldContain("workforce-entra|S-1-5-21-12345");
        resolution.Groups.ShouldContain("S-1-5-21-12345");
    }
}
