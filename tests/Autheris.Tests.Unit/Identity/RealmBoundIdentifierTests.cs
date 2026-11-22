using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Autheris.Application.Catalog.External;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Identity.Providers;
using Autheris.Application.Identity.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Autheris.Domain.Tenancy;
using Autheris.Tests.Unit.Security;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;
using AuthOptions = Autheris.Domain.Options.AuthenticationOptions;

namespace Autheris.Tests.Unit.Identity;

[Trait("Category", "Security")]
public sealed class RealmBoundIdentifierTests
{
    private const string SharedOid = "11223344-5566-7788-99aa-bbccddeeff00";
    private const string SharedSub = "shared-subject-42";
    private const string SharedAppId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string SharedCloudGroupSid = "S-1-12-1-287454020-2005423462-3434850969-16772829";
    private const string AdGroupSid = "S-1-5-21-100-200-300-512";
    private const string TenantAId = "11111111-1111-1111-1111-111111111111";
    private const string TenantBId = "22222222-2222-2222-2222-222222222222";
    private static readonly string EntraIssuerA = $"https://login.microsoftonline.com/{TenantAId}/v2.0";
    private static readonly string EntraIssuerB = $"https://login.microsoftonline.com/{TenantBId}/v2.0";
    private const string OidcIssuerA = "https://idp-a.partner.example/oauth2";
    private const string OidcIssuerB = "https://idp-b.partner.example/oauth2";

    [Fact]
    public async Task Same_oid_two_realms_distinct_sids()
    {
        var oidcA = new IdentityRealm(
            "oidc-partner-a",
            IdentityRealmKind.GenericOidc,
            TenantBindingSpecification.Fixed(new TenantId("tenant-a")),
            groupSource: RealmGroupSource.None,
            provisioning: RealmProvisioningMode.None,
            issuers: [OidcIssuerA]);
        var oidcB = new IdentityRealm(
            "oidc-partner-b",
            IdentityRealmKind.GenericOidc,
            TenantBindingSpecification.Fixed(new TenantId("tenant-b")),
            groupSource: RealmGroupSource.None,
            provisioning: RealmProvisioningMode.None,
            issuers: [OidcIssuerB]);
        var entraA = new IdentityRealm(
            "entra-corp-a",
            IdentityRealmKind.EntraWorkforce,
            TenantBindingSpecification.Fixed(new TenantId("tenant-a")),
            groupSource: RealmGroupSource.None,
            provisioning: RealmProvisioningMode.None,
            issuers: [EntraIssuerA]);
        var entraB = new IdentityRealm(
            "entra-corp-b",
            IdentityRealmKind.EntraWorkforce,
            TenantBindingSpecification.Fixed(new TenantId("tenant-b")),
            groupSource: RealmGroupSource.None,
            provisioning: RealmProvisioningMode.None,
            issuers: [EntraIssuerB]);

        var registry = new FakeIdentityRealmRegistry([oidcA, oidcB, entraA, entraB], oidcA);
        var pipeline = TestIdentityPipeline.Create(registry);
        var subjectResolver = new IdentitySubjectResolver();

        ClaimsPrincipal UserIn(string issuer, params Claim[] extra) => new(new ClaimsIdentity(
        [
            new Claim("iss", issuer),
            new Claim("oid", SharedOid),
            new Claim("sub", SharedSub),
            .. extra
        ], "Bearer"));

        // 1. Two GenericOidc realms with identical oid/sub
        var pOidcA = await pipeline.ProcessAsync(UserIn(OidcIssuerA), "Bearer", null, CancellationToken.None);
        var pOidcB = await pipeline.ProcessAsync(UserIn(OidcIssuerB), "Bearer", null, CancellationToken.None);

        pOidcA.GetUserSid().ShouldNotBe(pOidcB.GetUserSid());
        pOidcA.GetAllUserSids().Overlaps(pOidcB.GetAllUserSids()).ShouldBeFalse();
        subjectResolver.ResolveSubject(pOidcA).Id.ShouldNotBe(subjectResolver.ResolveSubject(pOidcB).Id);

        // 2. Two EntraWorkforce realms (cloud-only users without onprem_sid) with identical oid
        var pEntraA = await pipeline.ProcessAsync(UserIn(EntraIssuerA, new Claim("tid", TenantAId)), "Bearer", null, CancellationToken.None);
        var pEntraB = await pipeline.ProcessAsync(UserIn(EntraIssuerB, new Claim("tid", TenantBId)), "Bearer", null, CancellationToken.None);

        pEntraA.GetUserSid().ShouldNotBe(pEntraB.GetUserSid());
        pEntraA.GetAllUserSids().Overlaps(pEntraB.GetAllUserSids()).ShouldBeFalse();
        subjectResolver.ResolveSubject(pEntraA).Id.ShouldNotBe(subjectResolver.ResolveSubject(pEntraB).Id);

        // 3. EntraWorkforce vs GenericOidc with identical oid
        pEntraA.GetUserSid().ShouldNotBe(pOidcA.GetUserSid());
        pEntraA.GetAllUserSids().Overlaps(pOidcA.GetAllUserSids()).ShouldBeFalse();
        subjectResolver.ResolveSubject(pEntraA).Id.ShouldNotBe(subjectResolver.ResolveSubject(pOidcA).Id);

        // 4. Service principals (idtyp=app) across two Entra realms and GenericOidc must not collide on oid/sub/appid
        var spEntraA = await pipeline.ProcessAsync(
            UserIn(EntraIssuerA, new Claim("idtyp", "app"), new Claim("appid", SharedAppId), new Claim("tid", TenantAId)),
            "Bearer", null, CancellationToken.None);
        var spEntraB = await pipeline.ProcessAsync(
            UserIn(EntraIssuerB, new Claim("idtyp", "app"), new Claim("appid", SharedAppId), new Claim("tid", TenantBId)),
            "Bearer", null, CancellationToken.None);
        var spOidcA = await pipeline.ProcessAsync(
            UserIn(OidcIssuerA, new Claim("idtyp", "app"), new Claim("appid", SharedAppId)),
            "Bearer", null, CancellationToken.None);

        spEntraA.GetUserSid().ShouldNotBe(spEntraB.GetUserSid());
        spEntraA.GetUserSid().ShouldNotBe(spOidcA.GetUserSid());
        spEntraA.GetAllUserSids().Overlaps(spEntraB.GetAllUserSids()).ShouldBeFalse();
        subjectResolver.ResolveSubject(spEntraA).Id.ShouldNotBe(subjectResolver.ResolveSubject(spEntraB).Id);
        subjectResolver.ResolveSubject(spEntraA).Id.ShouldNotBe(subjectResolver.ResolveSubject(spOidcA).Id);

        // App-only identity without oid/sub falls back to ClaimsIdentity client_id overload without extra principal allocation
        var appOnlyIdentity = new ClaimsIdentity([new Claim("idtyp", "app"), new Claim("appid", SharedAppId)], "Bearer");
        AppOnlyPrincipal.GetClientId(appOnlyIdentity).ShouldBe(SharedAppId);
        var spClientOnlyA = await pipeline.ProcessAsync(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", EntraIssuerA), new Claim("tid", TenantAId), new Claim("idtyp", "app"), new Claim("appid", SharedAppId)], "Bearer")),
            "Bearer", null, CancellationToken.None);
        spClientOnlyA.GetUserSid().ShouldBe(new Sid($"entra-corp-a|{SharedAppId}"));
    }

    [Fact]
    public async Task Scim_group_cannot_alias_foreign_realm_group()
    {
        var tenant = new TenantId("tenant-a");
        var oidcRealmA = new IdentityRealm(
            "oidc-realm-a",
            IdentityRealmKind.GenericOidc,
            TenantBindingSpecification.Fixed(tenant),
            groupSource: RealmGroupSource.Resolver,
            provisioning: RealmProvisioningMode.Scim,
            issuers: [OidcIssuerA],
            resolverChain: ["scim-store"]);

        var oidcRealmB = new IdentityRealm(
            "oidc-realm-b",
            IdentityRealmKind.GenericOidc,
            TenantBindingSpecification.Fixed(tenant),
            groupSource: RealmGroupSource.Resolver,
            provisioning: RealmProvisioningMode.Scim,
            issuers: [OidcIssuerB],
            resolverChain: ["scim-store"]);

        foreach (var attemptedAliasedSid in new[] { AdGroupSid, SharedCloudGroupSid })
        {
            var store = NullFake.Create<IIdentityStoreRepository>((method, _) => method.Name switch
            {
                nameof(IScimGroupStore.GetEffectiveGroupSidsForUserAsync) => Task.FromResult(
                    new EffectiveScimGroupsResult(new HashSet<string>([attemptedAliasedSid]), MaxDepthExceeded: false, HasNonCanonicalSid: false)),
                nameof(IScimProvisioningStore.GetRealmSyncStateAsync) => Task.FromResult<ScimRealmSyncState?>(
                    new ScimRealmSyncState(tenant, oidcRealmA.Id, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1)),
                _ => NullFake.UseDefault
            });

            var scimProvider = new ScimStoreRoleProvider(store);
            var identity = new ResolvedIdentity("user-1", null, null, null, tenant, oidcRealmA.Id, []);
            var providerResult = await scimProvider.GetRolesAsync(identity, oidcRealmA, CancellationToken.None);
            providerResult.Failed.ShouldBeTrue();
            providerResult.Groups.ShouldNotContain(attemptedAliasedSid);

            var options = Options.Create(new GatewayOptions
            {
                Authentication = new AuthOptions { MaxGroupSidsPerCaller = 1024 }
            });
            var roleResolver = new RoleResolver([scimProvider], new RoleResolverCache(), null, options);
            var groupResolver = new GroupSidResolver(roleResolver, [scimProvider], options, scimGroupStore: store, scimProvisioningStore: store);

            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("iss", OidcIssuerA), new Claim("oid", "user-1")], "Bearer"));
            var resolution = await groupResolver.ResolveAsync(principal, oidcRealmA, tenant, "Bearer", CancellationToken.None);

            resolution.Status.ShouldBe(GroupResolutionStatus.Degraded);
            resolution.GroupSids.Select(s => s.Value).ShouldNotContain(attemptedAliasedSid);

            // ScimService refuses AD/Entra objectSid injection on non-workforce realms.
            var registry = new FakeIdentityRealmRegistry([oidcRealmA, oidcRealmB], oidcRealmA);
            var scimSvc = new ScimService(
                store,
                NullFake.Create<IIdentityDeprovisioningService>(),
                realmRegistry: registry);
            await Should.ThrowAsync<ValidationException>(() => scimSvc.CreateGroupAsync(
                tenant,
                oidcRealmA.Id,
                new ScimGroup("finance-owners", null, "Finance Owners", null, null, null, ObjectSid: attemptedAliasedSid)));
        }

        // Identical group_id across two non-workforce realms stays isolated by (realm_id, group_id) without aliasing.
        var multiRealmStore = NullFake.Create<IIdentityStoreRepository>((method, args) =>
        {
            if (method.Name == nameof(IScimGroupStore.GetEffectiveGroupSidsForUserAsync))
            {
                var rId = (string)args![1]!;
                return Task.FromResult(new EffectiveScimGroupsResult(
                    new HashSet<string>([SidFormat.FormatRealmGroupKey(rId, "finance-owners")]),
                    MaxDepthExceeded: false,
                    HasNonCanonicalSid: false,
                    GroupIds: new HashSet<string>(["finance-owners"])));
            }

            if (method.Name == nameof(IScimProvisioningStore.GetRealmSyncStateAsync))
            {
                var rId = (string)args![1]!;
                return Task.FromResult<ScimRealmSyncState?>(
                    new ScimRealmSyncState(tenant, rId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1));
            }

            return NullFake.UseDefault;
        });

        var multiRegistry = new FakeIdentityRealmRegistry([oidcRealmA, oidcRealmB], oidcRealmA);
        var cleanScimSvc = new ScimService(
            multiRealmStore,
            NullFake.Create<IIdentityDeprovisioningService>(),
            realmRegistry: multiRegistry);
        var createdInA = await cleanScimSvc.CreateGroupAsync(
            tenant, oidcRealmA.Id, new ScimGroup("finance-owners", null, "Finance Owners A", null, null, null));
        var createdInB = await cleanScimSvc.CreateGroupAsync(
            tenant, oidcRealmB.Id, new ScimGroup("finance-owners", null, "Finance Owners B", null, null, null));
        createdInA.ObjectSid.ShouldBe("realm:oidc-realm-a:finance-owners");
        createdInB.ObjectSid.ShouldBe("realm:oidc-realm-b:finance-owners");
        createdInA.ObjectSid.ShouldNotBe(createdInB.ObjectSid);

        var opts = Options.Create(new GatewayOptions { Authentication = new AuthOptions { MaxGroupSidsPerCaller = 1024 } });
        var multiProvider = new ScimStoreRoleProvider(multiRealmStore);
        var multiRoleResolver = new RoleResolver(multiRegistry, [multiProvider], new RoleResolverCache(), opts, multiRealmStore);
        var multiPipeline = TestIdentityPipeline.Create(
            multiRegistry,
            multiRoleResolver,
            groupResolver: new GroupSidResolver(
                multiRoleResolver,
                [multiProvider], opts, scimGroupStore: multiRealmStore, scimProvisioningStore: multiRealmStore));

        var resolvedA = await multiPipeline.ProcessAsync(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", OidcIssuerA), new Claim("oid", "user-1")], "Bearer")),
            "Bearer", null, CancellationToken.None);
        var resolvedB = await multiPipeline.ProcessAsync(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", OidcIssuerB), new Claim("oid", "user-1")], "Bearer")),
            "Bearer", null, CancellationToken.None);

        resolvedA.GetGroupSids().ShouldBe([new Sid("realm:oidc-realm-a:finance-owners")]);
        resolvedB.GetGroupSids().ShouldBe([new Sid("realm:oidc-realm-b:finance-owners")]);
        resolvedA.GetGroupSids().Overlaps(resolvedB.GetGroupSids()).ShouldBeFalse();

        // Non-canonical encodings or keys longer than 184 chars must be rejected cleanly (ValidationException -> HTTP 400).
        SidFormat.IsRealmGroupKey("realm:oidc-realm-a:a%2Fb").ShouldBeTrue();
        SidFormat.IsRealmGroupKey("realm:oidc-realm-a:%46inance").ShouldBeFalse();
        SidFormat.IsRealmGroupKey("realm:oidc-realm-a:a/b").ShouldBeFalse();
        SidFormat.IsRealmGroupKey("realm:oidc-realm-a:a%3ab").ShouldBeFalse();
        SidFormat.IsRealmGroupKey("realm:oidc-realm-a:" + new string('g', 170)).ShouldBeFalse();
        await Should.ThrowAsync<ValidationException>(() => cleanScimSvc.CreateGroupAsync(
            tenant, oidcRealmA.Id, new ScimGroup(new string('g', 175), null, "Too Long", null, null, null)));

        // ScimService.UpdateGroupAsync passes both existing and updated ObjectSid into membership validation.
        const string oldSid = "S-1-5-21-100-200-300-5100";
        const string newSid = "S-1-5-21-100-200-300-5200";
        var seenValidationKeys = new List<string>();
        var rightRepo = NullFake.Create<IFunctionalRightRepository>((method, args) =>
        {
            if (method.Name == nameof(IFunctionalRightRepository.GetApprovedMappingsForGroupsAndRolesAsync))
            {
                var groupsAndRoles = (IReadOnlyCollection<string>)args![2]!;
                seenValidationKeys.AddRange(groupsAndRoles);
                var matches = groupsAndRoles.Contains(oldSid, StringComparer.OrdinalIgnoreCase);
                return Task.FromResult<IReadOnlyList<FunctionalRightMapping>>(matches
                    ? [new FunctionalRightMapping(tenant, "entra-upd", oldSid, FunctionalRight.SecurityAdmin, "approver-alice", DateTimeOffset.UtcNow, "Privileged", "creator-bob", DateTimeOffset.UtcNow)]
                    : []);
            }

            return NullFake.UseDefault;
        });

        var entraUpdRealm = new IdentityRealm("entra-upd", IdentityRealmKind.EntraWorkforce, TenantBindingSpecification.Fixed(tenant), issuers: [EntraIssuerA]);
        var updGroupStore = NullFake.Create<IIdentityStoreRepository>((method, _) => method.Name switch
        {
            nameof(IScimGroupStore.GetGroupAsync) => Task.FromResult<ScimGroup?>(new ScimGroup("g-upd", "g-upd", "Admins", null, null, null, oldSid)),
            nameof(IScimGroupStore.GetGroupMembersAsync) => Task.FromResult<IReadOnlyList<string>>(["u1", "u2"]),
            nameof(IScimGroupStore.GetChildGroupIdsAsync) => Task.FromResult<IReadOnlyList<string>>([]),
            _ => NullFake.UseDefault
        });

        var updScimSvc = new ScimService(
            updGroupStore,
            NullFake.Create<IIdentityDeprovisioningService>(),
            membershipValidator: new ScimGroupMembershipValidator(rightRepo),
            realmRegistry: new FakeIdentityRealmRegistry(entraUpdRealm));

        await Should.ThrowAsync<MassMembershipChangeQuarantineException>(() => updScimSvc.UpdateGroupAsync(
            tenant,
            entraUpdRealm.Id,
            "g-upd",
            new ScimGroup("g-upd", "g-upd", "Admins", [], null, null, newSid)));
        seenValidationKeys.ShouldContain(oldSid);
        seenValidationKeys.ShouldContain(newSid);
    }

    [Fact]
    public async Task Entra_cloud_sid_only_from_tenant_bound_entra_realms()
    {
        var tenant = new TenantId("tenant-a");
        var options = Options.Create(new GatewayOptions
        {
            Authentication = new AuthOptions { MaxGroupSidsPerCaller = 1024 }
        });

        var tenantBoundEntra = new IdentityRealm(
            "entra-bound",
            IdentityRealmKind.EntraWorkforce,
            TenantBindingSpecification.Fixed(tenant),
            groupSource: RealmGroupSource.Token,
            provisioning: RealmProvisioningMode.Scim,
            issuers: [EntraIssuerA]);
        var tenantBoundExternalId = new IdentityRealm(
            "entra-ext-bound",
            IdentityRealmKind.EntraExternalId,
            TenantBindingSpecification.Fixed(tenant),
            groupSource: RealmGroupSource.Token,
            provisioning: RealmProvisioningMode.Scim,
            issuers: [$"https://ext.ciamlogin.com/{TenantBId}/v2.0"]);
        var genericOidc = new IdentityRealm(
            "oidc-token",
            IdentityRealmKind.GenericOidc,
            TenantBindingSpecification.Fixed(tenant),
            groupSource: RealmGroupSource.Token,
            provisioning: RealmProvisioningMode.None,
            issuers: [OidcIssuerA]);
        var commonWildcardEntra = new IdentityRealm(
            "entra-common",
            IdentityRealmKind.EntraWorkforce,
            TenantBindingSpecification.Fixed(tenant),
            groupSource: RealmGroupSource.Token,
            provisioning: RealmProvisioningMode.None,
            issuers: ["https://login.microsoftonline.com/common/v2.0"]);
        var emptyIssuerEntra = new IdentityRealm(
            "entra-empty",
            IdentityRealmKind.EntraWorkforce,
            TenantBindingSpecification.Fixed(tenant),
            groupSource: RealmGroupSource.Token,
            provisioning: RealmProvisioningMode.None,
            issuers: []);

        var resolver = new GroupSidResolver(null, [], options);
        var tokenWithGuid = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", "user-1"), new Claim("groups", SharedOid)], "Bearer"));
        var tokenWithCloudSid = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", "user-1"), new Claim("groups", SharedCloudGroupSid)], "Bearer"));

        // Tenant-bound Entra realms produce cloud SID directly and via token/Graph group resolution
        SidFormat.TryFromEntraObjectId(Guid.Parse(SharedOid), tenantBoundEntra, EntraIssuerA, out var boundSid, TenantAId).ShouldBeTrue();
        boundSid.Value.ShouldBe(SharedCloudGroupSid);
        SidFormat.TryFromEntraObjectId(Guid.Parse(SharedOid), genericOidc, OidcIssuerA, out _).ShouldBeFalse();
        SidFormat.TryFromEntraObjectId(Guid.Parse(SharedOid), commonWildcardEntra, null, out _).ShouldBeFalse();
        SidFormat.TryFromEntraObjectId(Guid.Parse(SharedOid), emptyIssuerEntra, null, out _).ShouldBeFalse();
        SidFormat.TryFromEntraObjectId(Guid.Parse(SharedOid), tenantBoundEntra, EntraIssuerB, out _, TenantBId).ShouldBeFalse();

        var okEntra = await resolver.ResolveAsync(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", EntraIssuerA), new Claim("tid", TenantAId), new Claim("oid", "user-1"), new Claim("groups", SharedOid)], "Bearer")),
            tenantBoundEntra, tenant, "Bearer", CancellationToken.None);
        okEntra.Status.ShouldBe(GroupResolutionStatus.Complete);
        okEntra.GroupSids.Select(s => s.Value).ShouldBe([SharedCloudGroupSid]);

        var okExt = await resolver.ResolveAsync(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", $"https://ext.ciamlogin.com/{TenantBId}/v2.0"), new Claim("tid", TenantBId), new Claim("oid", "user-1"), new Claim("groups", SharedOid)], "Bearer")),
            tenantBoundExternalId, tenant, "Bearer", CancellationToken.None);
        okExt.Status.ShouldBe(GroupResolutionStatus.Complete);
        okExt.GroupSids.Select(s => s.Value).ShouldBe([SharedCloudGroupSid]);

        // Conflicting Entra tid claims fail closed, while valid tid alongside a business tenant_id succeeds
        var withBusinessTenant = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("iss", EntraIssuerA), new Claim("tenant_id", "corp"), new Claim("tid", TenantAId), new Claim("oid", "user-1"), new Claim("groups", SharedOid)], "Bearer"));
        TenantClaimReader.ReadEntraDirectoryId(withBusinessTenant).ShouldBe(TenantAId);
        var okWithBusinessTenant = await resolver.ResolveAsync(withBusinessTenant, tenantBoundEntra, tenant, "Bearer", CancellationToken.None);
        okWithBusinessTenant.Status.ShouldBe(GroupResolutionStatus.Complete);
        okWithBusinessTenant.GroupSids.Select(s => s.Value).ShouldBe([SharedCloudGroupSid]);

        var conflictingTid = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("iss", EntraIssuerA), new Claim("tid", TenantAId), new Claim("http://schemas.microsoft.com/identity/claims/tenantid", TenantBId), new Claim("oid", "user-1"), new Claim("groups", SharedOid)], "Bearer"));
        TenantClaimReader.ReadEntraDirectoryId(conflictingTid).ShouldBeNull();
        TenantClaimReader.TryReadEntraDirectoryId(conflictingTid, out _).ShouldBeFalse();
        var degradedConflictingTid = await resolver.ResolveAsync(conflictingTid, tenantBoundEntra, tenant, "Bearer", CancellationToken.None);
        degradedConflictingTid.Status.ShouldBe(GroupResolutionStatus.Degraded);
        degradedConflictingTid.GroupSids.ShouldBeEmpty();

        // Pathless Entra / CIAM authorities are never tenant-bound
        foreach (var pathlessIssuer in new[]
        {
            "https://login.microsoftonline.com/",
            "https://sts.windows.net/",
            "https://login.microsoftonline.com/v2.0",
            "https://ext.ciamlogin.com/"
        })
        {
            SidFormat.IsTenantBoundEntraIssuer(pathlessIssuer).ShouldBeFalse();
            var pathlessRealm = new IdentityRealm(
                "entra-pathless",
                IdentityRealmKind.EntraWorkforce,
                TenantBindingSpecification.Fixed(tenant),
                groupSource: RealmGroupSource.Token,
                provisioning: RealmProvisioningMode.None,
                issuers: [pathlessIssuer]);
            SidFormat.TryFromEntraObjectId(Guid.Parse(SharedOid), pathlessRealm, pathlessIssuer, out _).ShouldBeFalse();
        }

        // Non-Entra, empty-issuer, or wildcard /common Entra realms reject cloud SID & GUID-to-cloud-SID conversion
        foreach (var untrustedRealm in new[] { genericOidc, commonWildcardEntra, emptyIssuerEntra })
        {
            var rGuid = await resolver.ResolveAsync(tokenWithGuid, untrustedRealm, tenant, "Bearer", CancellationToken.None);
            rGuid.Status.ShouldBe(GroupResolutionStatus.Degraded);
            rGuid.GroupSids.ShouldBeEmpty();

            var rCloud = await resolver.ResolveAsync(tokenWithCloudSid, untrustedRealm, tenant, "Bearer", CancellationToken.None);
            rCloud.Status.ShouldBe(GroupResolutionStatus.Degraded);
            rCloud.GroupSids.ShouldBeEmpty();

            var graphProvider = new GraphRoleProvider(new FakeGraphAdapter
            {
                TransitiveMemberOf = _ => Task.FromResult<IReadOnlyList<string>>([SharedOid])
            });
            var graphResult = await graphProvider.GetRolesAsync(
                new ResolvedIdentity("user-1", null, null, null, tenant, untrustedRealm.Id, []),
                untrustedRealm,
                CancellationToken.None);
            graphResult.Failed.ShouldBeTrue();
            graphResult.Groups.ShouldBeEmpty();
        }
    }
}
