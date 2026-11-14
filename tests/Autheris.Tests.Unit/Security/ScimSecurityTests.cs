namespace Autheris.Tests.Unit.Security;

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Identity.Providers;
using Autheris.Application.Identity.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;
using AuthenticationOptions = Autheris.Domain.Options.AuthenticationOptions;

[Trait("Category", "Security")]
public sealed class ScimSecurityTests
{
    private const string SidA = "S-1-5-21-100-200-300-4101";
    private const string SidB = "S-1-5-21-100-200-300-4102";
    private const string OidA = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private static readonly TenantId Tenant = new("tenant-1");

    [Fact]
    public async Task Deprovision_invalidates_cluster_wide()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new StaticTimeProvider(now);
        var store = new ScimDeprovisioningTests.SharedDeprovisionStore { LastContactAt = now };
        store.SeedUser("u-1", OidA, active: true);
        store.SeedGroup("g-1", SidA);
        store.SeedGroup("g-2", SidB);
        store.Memberships.Add(("g-1", "u-1"));
        store.Memberships.Add(("g-2", "u-1"));

        var realm = new IdentityRealm(
            "realm-1",
            IdentityRealmKind.EntraWorkforce,
            TenantBindingSpecification.Fixed(Tenant),
            RealmGroupSource.Token,
            RealmProvisioningMode.Scim,
            issuers: ["https://issuer.example.org"]);

        var node1Cache = new RoleResolverCache();
        var node2Cache = new RoleResolverCache();
        var options = Options.Create(new GatewayOptions { Authentication = new AuthenticationOptions() });

        var node2Resolver = new GroupSidResolver(
            new RoleResolver([new ScimStoreRoleProvider(store)], node2Cache, null, options, store),
            [],
            options,
            timeProvider: time,
            scimCorrelator: new ScimIdentityCorrelator(store, store),
            scimGroupStore: store,
            scimProvisioningStore: store,
            syncStateTracker: new ScimSyncStateTracker(store, time),
            roleCache: node2Cache);

        var node1Scim = new ScimService(
            store,
            new IdentityDeprovisioningService(store, roleResolverCache: node1Cache),
            roleCache: node1Cache,
            timeProvider: time);

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", OidA)], "Bearer"));

        // Warm Node 2 cache
        var initial = await node2Resolver.ResolveAsync(principal, realm, Tenant, "Bearer", CancellationToken.None);
        initial.Status.ShouldBe(GroupResolutionStatus.Complete);
        initial.GroupSids.Select(s => s.Value).OrderBy(x => x, StringComparer.Ordinal).ToArray().ShouldBe([SidA, SidB]);

        // Group delete on Node 1 -> immediately visible on Node 2 via epoch bump
        await node1Scim.DeleteGroupAsync(Tenant, "realm-1", "g-2");
        var afterGroupDelete = await node2Resolver.ResolveAsync(principal, realm, Tenant, "Bearer", CancellationToken.None);
        afterGroupDelete.Status.ShouldBe(GroupResolutionStatus.Complete);
        afterGroupDelete.GroupSids.Select(s => s.Value).ToArray().ShouldBe([SidA]);

        // User deactivation on Node 1 -> immediately denied (Degraded) on Node 2
        var patchDeactivate = new ScimPatchRequest([ScimSchemaUris.PatchOp], [new ScimPatchOperation("replace", "active", false)]);
        await node1Scim.PatchUserAsync(Tenant, "realm-1", "u-1", patchDeactivate);
        var afterDeactivate = await node2Resolver.ResolveAsync(principal, realm, Tenant, "Bearer", CancellationToken.None);
        afterDeactivate.Status.ShouldBe(GroupResolutionStatus.Degraded);
        afterDeactivate.GroupSids.ShouldBeEmpty();
    }

    private sealed class StaticTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
