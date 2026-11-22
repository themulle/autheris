namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Identity.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Types;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Tests.Unit.Persistence.DialectSql;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed partial class AdvisoryRegressionTests
{
    /// <summary>GHSA-7v65-hxqv regression checks for realm user key binding, tenant-scoped approval queries, cache isolation, SCIM additions, and fail-closed revocation.</summary>
    [Fact]
    [Trait("Category", "Security")]
    public async Task Ghsa_7v65_hxqv_regression()
    {
        await VerifyCrossRealmWindowsSidStrippingAndUserBindingAsync();
        await VerifyNonWindowsScimCorrelationWithoutOnpremSidAsync();
        await VerifyRealmUserKeyResolverClaimsAsync();
        await VerifyConsentQueryTenantIsolationAsync();
        await VerifyRoleAndScimCacheNamespaceIsolationAsync();
        await VerifyScimAdditionInvalidationAsync();
        VerifyFailClosedRevocationAuthority();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task NonWindowsOidcRealm_StripsInboundWindowsSids_AndPreservesMixedCaseSubIsolation()
    {
        await VerifyCrossRealmWindowsSidStrippingAndUserBindingAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task NonWindowsOidcRealm_WithScimAndNoOnpremSid_CorrelatesAndBindsUserSid()
    {
        await VerifyNonWindowsScimCorrelationWithoutOnpremSidAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GraphQl_MyAccessRequests_And_PendingApprovalsForMe_EnforceTenantIsolation()
    {
        await VerifyConsentQueryTenantIsolationAsync();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RoleCache_DisjointNamespaces_PreventUserKeyPrefixCollision_AndInvalidatePrefixedKeys()
    {
        await VerifyRoleAndScimCacheNamespaceIsolationAsync();
    }

    private static async Task VerifyCrossRealmWindowsSidStrippingAndUserBindingAsync()
    {
        var realmA = new IdentityRealm(
            "oidc-a",
            IdentityRealmKind.GenericOidc,
            new TenantBindingSpecification(TenantBindingKind.Fixed, new TenantId("tenant-shared")),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.None,
            issuers: ["https://idp-a.example"],
            resolverChain: ["stub-role-provider"],
            userKeyClaim: "sub");

        var realmB = new IdentityRealm(
            "oidc-b",
            IdentityRealmKind.GenericOidc,
            new TenantBindingSpecification(TenantBindingKind.Fixed, new TenantId("tenant-shared")),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.None,
            issuers: ["https://idp-b.example"],
            resolverChain: ["stub-role-provider"],
            userKeyClaim: "sub");

        var registry = new FakeIdentityRealmRegistry([realmA, realmB], realmA);
        var roleCache = new RecordingRoleCache();
        var options = Options.Create(new GatewayOptions());
        var roleProvider = new StaticRoleProvider("stub-role-provider", ["S-1-5-21-1000-2000-3000-4001"]);
        var roleResolver = new RoleResolver(registry, [roleProvider], roleCache, options);
        var groupResolver = new GroupSidResolver(roleResolver, [roleProvider], options);
        var pipeline = new IdentityPipeline(registry, groupResolver, roleResolver, options: options);

        const string victimAdSid = "S-1-5-21-1000-2000-3000-1001";
        var forgedOidcPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("iss", "https://idp-b.example"),
                new Claim("sub", "Alice"),
                new Claim("oid", "victim-oid-001"),
                new Claim(ClaimTypes.PrimarySid, victimAdSid),
                new Claim("primarysid", victimAdSid),
                new Claim("objectSid", victimAdSid),
                new Claim("onprem_sid", victimAdSid)
            ],
            "Bearer"));

        var result = await pipeline.ProcessAsync(forgedOidcPrincipal, "Bearer", null, CancellationToken.None);

        result.Identity!.IsAuthenticated.ShouldBeTrue();
        result.GetUserSid().ShouldBe(new Sid("oidc-b|Alice"));
        result.GetAllUserSids().ShouldBe([new Sid("oidc-b|Alice")]);
        result.GetAllUserSids().ShouldNotContain(new Sid(victimAdSid));
        result.GetAllUserSids().ShouldNotContain(new Sid("Alice"));
        result.GetAllUserSids().ShouldNotContain(new Sid("oidc-a|Alice"));
        roleCache.KeysSeen.ShouldContain(("oidc-b", "role:Alice"));
        roleCache.KeysSeen.ShouldNotContain(("oidc-b", "role:victim-oid-001"));

        var lowerOidc = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("iss", "https://idp-b.example"),
                new Claim("sub", "alice")
            ],
            "Bearer"));
        var lowerResult = await pipeline.ProcessAsync(lowerOidc, "Bearer", null, CancellationToken.None);
        lowerResult.GetUserSid().ShouldBe(new Sid("oidc-b|alice"));
        (result.GetUserSid()!.Value == lowerResult.GetUserSid()!.Value).ShouldBeFalse();

        // Delimiter collision check: literal "oidc-b|alice" still receives unconditional realm binding.
        var delimiterPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("iss", "https://idp-b.example"),
                new Claim("sub", "oidc-b|alice")
            ],
            "Bearer"));
        var delimiterResult = await pipeline.ProcessAsync(delimiterPrincipal, "Bearer", null, CancellationToken.None);
        delimiterResult.GetUserSid().ShouldBe(new Sid("oidc-b|oidc-b|alice"));
        (delimiterResult.GetUserSid()!.Value == lowerResult.GetUserSid()!.Value).ShouldBeFalse();
        roleCache.KeysSeen.ShouldContain(("oidc-b", "role:oidc-b|alice"));

        // Inbound cert: values without clientCert must still be stripped and realm-bound.
        var certSpoofOidc = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("iss", "https://idp-b.example"),
                new Claim(ClaimTypes.PrimarySid, "cert:fake"),
                new Claim("primarysid", "cert:fake"),
                new Claim("sub", "cert:fake")
            ],
            "Bearer"));
        var certSpoofResult = await pipeline.ProcessAsync(certSpoofOidc, "Bearer", null, CancellationToken.None);
        certSpoofResult.Identity!.IsAuthenticated.ShouldBeTrue();
        certSpoofResult.GetUserSid().ShouldBe(new Sid("oidc-b|cert:fake"));
        certSpoofResult.GetAllUserSids().ShouldBe([new Sid("oidc-b|cert:fake")]);
        certSpoofResult.GetAllUserSids().ShouldNotContain(new Sid("cert:fake"));

        // Missing configured UserKeyClaim fails closed (Unauthenticated) regardless of GroupSource (None / Token).
        foreach (var groupSource in new[] { RealmGroupSource.None, RealmGroupSource.Token })
        {
            var strictRealm = new IdentityRealm(
                "strict-oidc",
                IdentityRealmKind.GenericOidc,
                new TenantBindingSpecification(TenantBindingKind.Fixed, new TenantId("tenant-shared")),
                groupSource,
                RealmProvisioningMode.None,
                issuers: ["https://strict.example"],
                userKeyClaim: "sub");
            var strictPipeline = new IdentityPipeline(new FakeIdentityRealmRegistry(strictRealm), groupResolver, options: options);
            var missingSubPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("iss", "https://strict.example"),
                    new Claim("oid", "fallback-oid"),
                    new Claim("upn", "user@example.com")
                ],
                "Bearer"));
            var strictResult = await strictPipeline.ProcessAsync(missingSubPrincipal, "Bearer", null, CancellationToken.None);
            strictResult.Identity!.IsAuthenticated.ShouldBeFalse();
            strictResult.GetUserSid().ShouldBeNull();
        }

        // Non-SID subject matching in ConsentSqlCore stays exact (case-sensitive), while canonical Windows SIDs and GUIDs match case-insensitively.
        var table = new TableIdentifier("sales", "dbo", "orders");
        var consentRows = BuildConsentSubjectRows(
            ("oidc-b|Alice", Guid.NewGuid()),
            ("S-1-5-21-1000-2000-3000-1001", Guid.NewGuid()),
            ("a1b2c3d4-0000-0000-0000-000000000001", Guid.NewGuid()));
        var matchingMixedLower = new List<Consent>();
        ConsentSqlCore.ReadSubjectConsentRows(
            consentRows.CreateDataReader(),
            table,
            DateTimeOffset.UtcNow,
            [new Sid("oidc-b|alice"), new Sid("s-1-5-21-1000-2000-3000-1001"), new Sid("A1B2C3D4-0000-0000-0000-000000000001")],
            [],
            matchingMixedLower);
        matchingMixedLower.Select(c => c.GranteeSid!.Value.Value).ShouldBe(
            ["S-1-5-21-1000-2000-3000-1001", "a1b2c3d4-0000-0000-0000-000000000001"]);
    }

    private static async Task VerifyNonWindowsScimCorrelationWithoutOnpremSidAsync()
    {
        var oidcScimRealm = new IdentityRealm(
            "oidc-scim",
            IdentityRealmKind.GenericOidc,
            new TenantBindingSpecification(TenantBindingKind.Fixed, TenantA),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.Scim,
            issuers: ["https://oidc-scim.example"],
            resolverChain: ["scim-provider"],
            userKeyClaim: "sub");

        var entraScimRealm = new IdentityRealm(
            "entra-scim",
            IdentityRealmKind.EntraWorkforce,
            new TenantBindingSpecification(TenantBindingKind.Fixed, TenantA),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.Scim,
            issuers: ["https://entra-scim.example"],
            resolverChain: ["scim-provider"],
            userKeyClaim: "oid");

        var registry = new FakeIdentityRealmRegistry([oidcScimRealm, entraScimRealm], oidcScimRealm);
        var cache = new RoleResolverCache();
        var scimStore = new InMemoryScimCacheAndEpochStore("cloud-oid-1", "S-1-5-21-1000-2000-3000-7777");
        var options = Options.Create(new GatewayOptions());
        var roleProvider = new StaticRoleProvider("scim-provider", ["S-1-5-21-1000-2000-3000-8888"]);
        var roleResolver = new RoleResolver(registry, [roleProvider], cache, options, scimStore);
        var groupResolver = new GroupSidResolver(
            roleResolver,
            [roleProvider],
            options,
            scimCorrelator: new ScimIdentityCorrelator(scimStore),
            scimGroupStore: scimStore,
            scimProvisioningStore: scimStore,
            roleCache: cache);
        var pipeline = new IdentityPipeline(registry, groupResolver, roleResolver, options: options);

        // OIDC SCIM realm without onprem_sid: synthesized PrimarySid must not collide with ScimIdentityCorrelator objectSid check.
        var oidcPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("iss", "https://oidc-scim.example"),
                new Claim("oid", "cloud-oid-1"),
                new Claim("sub", "sub-alice")
            ],
            "Bearer"));
        var oidcResult = await pipeline.ProcessAsync(oidcPrincipal, "Bearer", null, CancellationToken.None);
        oidcResult.Identity!.IsAuthenticated.ShouldBeTrue();
        oidcResult.HasClaim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded).ShouldBeFalse();
        oidcResult.GetUserSid().ShouldBe(new Sid("oidc-scim|sub-alice"));
        oidcResult.GetGroupSids().ShouldContain(new Sid("realm:oidc-scim:grp-7777"));
        oidcResult.GetGroupSids().ShouldContain(new Sid("S-1-5-21-1000-2000-3000-8888"));

        // Entra SCIM cloud-only user without onprem_sid: synthesized PrimarySid is ignored; malformed inbound objectSid still fails closed.
        var entraCloudPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("iss", "https://entra-scim.example"),
                new Claim("oid", "cloud-oid-1"),
                new Claim("sub", "sub-alice")
            ],
            "Bearer"));
        var entraCloudResult = await pipeline.ProcessAsync(entraCloudPrincipal, "Bearer", null, CancellationToken.None);
        entraCloudResult.Identity!.IsAuthenticated.ShouldBeTrue();
        entraCloudResult.HasClaim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded).ShouldBeFalse();
        entraCloudResult.GetGroupSids().ShouldContain(new Sid("S-1-5-21-1000-2000-3000-7777"));

        var malformedSidPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("iss", "https://entra-scim.example"),
                new Claim("oid", "cloud-oid-1"),
                new Claim("objectSid", "not-a-windows-sid")
            ],
            "Bearer"));
        var malformedResult = await pipeline.ProcessAsync(malformedSidPrincipal, "Bearer", null, CancellationToken.None);
        malformedResult.HasClaim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded).ShouldBeTrue();
    }

    private static Task VerifyRealmUserKeyResolverClaimsAsync()
    {
        var subRealm = new IdentityRealm(
            "r-sub",
            IdentityRealmKind.GenericOidc,
            new TenantBindingSpecification(TenantBindingKind.Fixed, TenantA),
            userKeyClaim: "sub");

        var principalWithOidAndSub = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", "oid-val"), new Claim("sub", "sub-val"), new Claim("appid", "app-val")],
            "Bearer"));
        RealmUserKeyResolver.Resolve(principalWithOidAndSub, subRealm).ShouldBe("sub-val");
        RealmUserKeyResolver.Resolve(principalWithOidAndSub, subRealm, "correlated-key").ShouldBe("correlated-key");

        var noSubPrincipal = ClaimsNormalizer.Normalize(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", "oid-only"), new Claim("appid", "shared-app"), new Claim("client_id", "shared-client"), new Claim("azp", "shared-azp"), new Claim("upn", "u@corp"), new Claim("name", "User"), new Claim("email", "u@corp")],
            "Bearer")));
        RealmUserKeyResolver.Resolve(noSubPrincipal, subRealm).ShouldBeNull();

        var customRealm = new IdentityRealm(
            "r-custom",
            IdentityRealmKind.GenericOidc,
            new TenantBindingSpecification(TenantBindingKind.Fixed, TenantA),
            userKeyClaim: "employee_uid");
        var customPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "sub-val"), new Claim("oid", "oid-val"), new Claim("employee_uid", "emp-42")],
            "Bearer"));
        RealmUserKeyResolver.Resolve(customPrincipal, customRealm).ShouldBe("emp-42");

        var missingCustom = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "sub-val"), new Claim("oid", "oid-val"), new Claim("upn", "emp@corp")],
            "Bearer"));
        RealmUserKeyResolver.Resolve(missingCustom, customRealm).ShouldBeNull();

        return Task.CompletedTask;
    }

    private static async Task VerifyConsentQueryTenantIsolationAsync()
    {
        var tenantB = new TenantId("tenant-b");
        var sid = new Sid("S-1-5-21-USER-1");
        var repo = new RecordingApprovalRepository
        {
            Requests =
            [
                new ConsentRequest
                {
                    Id = Guid.NewGuid(),
                    TableId = Guid.NewGuid(),
                    TableIdentifier = new TableIdentifier("db", "s", "t1"),
                    RequesterSid = sid,
                    RequestedGranteeType = GranteeType.User,
                    RequestedGranteeRef = sid.Value,
                    BusinessJustification = "a",
                    Status = "PENDING",
                    TenantId = TenantA
                },
                new ConsentRequest
                {
                    Id = Guid.NewGuid(),
                    TableId = Guid.NewGuid(),
                    TableIdentifier = new TableIdentifier("db", "s", "t2"),
                    RequesterSid = sid,
                    RequestedGranteeType = GranteeType.User,
                    RequestedGranteeRef = sid.Value,
                    BusinessJustification = "b",
                    Status = "PENDING",
                    TenantId = tenantB
                }
            ]
        };

        var query = new Query();
        var tenantAAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, sid.Value), new Claim("tenant_id", TenantA.Value)], "Bearer"))
            }
        };

        var mine = await query.MyAccessRequestsAsync(null, null, null, repo, tenantAAccessor, ct: CancellationToken.None);
        mine.Count.ShouldBe(1);
        mine[0].TableIdentifier.ShouldBe("db.s.t1");
        repo.LastRequesterTenant.ShouldBe(TenantA);

        var pending = await query.PendingApprovalsForMeAsync(null, null, repo, tenantAAccessor, null, ct: CancellationToken.None);
        pending.Count.ShouldBe(1);
        pending[0].TableIdentifier.ShouldBe("db.s.t1");
        repo.LastApproverTenant.ShouldBe(TenantA);

        var noTenantAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, sid.Value)], "Bearer"))
            }
        };
        await Should.ThrowAsync<GraphQLException>(() => query.MyAccessRequestsAsync(null, null, null, repo, noTenantAccessor, ct: CancellationToken.None));
        await Should.ThrowAsync<GraphQLException>(() => query.PendingApprovalsForMeAsync(null, null, repo, noTenantAccessor, null, ct: CancellationToken.None));

        using var conn = DialectKit.PostgreSql.NewConnection();
        var governanceRepo = new GovernanceRepository(
            new PostgreSqlGovernanceDialect(() => conn),
            options: Options.Create(new GatewayOptions()),
            logger: NullLogger<GovernanceRepository>.Instance);
        conn.EnqueueReader(new DataTable());
        await governanceRepo.GetRequestsByRequesterSidAsync(sid, TenantA);
        conn.EnqueueReader(new DataTable());
        await governanceRepo.GetPendingRequestsForApproverAsync(sid, TenantA);

        conn.Commands.Count.ShouldBe(2);
        conn.Commands.ShouldAllBe(c => c.CommandText.Contains("r.tenant_id = @tenantId", StringComparison.Ordinal));
        conn.Commands[0].Parameters.Single(p => p.Name == "@tenantId").Value.ShouldBe(TenantA.Value);
        conn.Commands[1].Parameters.Single(p => p.Name == "@tenantId").Value.ShouldBe(TenantA.Value);
    }

    private static async Task VerifyRoleAndScimCacheNamespaceIsolationAsync()
    {
        var scimRealm = new IdentityRealm(
            "scim-realm",
            IdentityRealmKind.EntraWorkforce,
            new TenantBindingSpecification(TenantBindingKind.Fixed, TenantA),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.Scim,
            resolverChain: ["scim-provider"]);

        var nonScimRealm = new IdentityRealm(
            "nonscim-realm",
            IdentityRealmKind.GenericOidc,
            new TenantBindingSpecification(TenantBindingKind.Fixed, TenantA),
            RealmGroupSource.Resolver,
            RealmProvisioningMode.None,
            resolverChain: ["scim-provider"]);

        var registry = new FakeIdentityRealmRegistry([scimRealm, nonScimRealm], scimRealm);
        var cache = new RoleResolverCache();
        var scimStore = new InMemoryScimCacheAndEpochStore("scim:alice", "S-1-5-21-1000-2000-3000-7001");
        var options = Options.Create(new GatewayOptions());
        var roleProvider = new StaticRoleProvider("scim-provider", ["S-1-5-21-1000-2000-3000-8001"]);
        var roleResolver = new RoleResolver(registry, [roleProvider], cache, options, scimStore);
        var groupResolver = new GroupSidResolver(
            roleResolver,
            [roleProvider],
            options,
            scimCorrelator: new ScimIdentityCorrelator(scimStore),
            scimGroupStore: scimStore,
            scimProvisioningStore: scimStore,
            roleCache: cache);

        // Pre-seed a SCIM cache entry for "scim:alice" and a non-SCIM role resolution for userKey "scim:alice".
        var principalForScim = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", "scim:alice")], "Bearer"));
        var groupResolution = await groupResolver.ResolveAsync(principalForScim, scimRealm, TenantA, "Bearer", CancellationToken.None);
        groupResolution.GroupSids.ShouldContain(new Sid("S-1-5-21-1000-2000-3000-7001"));

        var cachedScimEntry = await cache.GetAsync("scim-realm", "scim:e0:scim:alice");
        cachedScimEntry.ShouldNotBeNull();

        var roleIdentity = new ResolvedIdentity("scim:alice", null, null, null, TenantA, "scim-realm", []);
        var resolvedRoles = await roleResolver.ResolveRolesAsync(roleIdentity);
        resolvedRoles.Groups.ShouldContain("S-1-5-21-1000-2000-3000-8001");
        (await cache.GetAsync("scim-realm", "role:e0:scim:alice")).ShouldNotBeNull();
        // Pre-existing SCIM entry under scim:e0:scim:alice remains untouched by RoleResolver.
        (await cache.GetAsync("scim-realm", "scim:e0:scim:alice"))!.Groups.ShouldContain("S-1-5-21-1000-2000-3000-7001");

        var nonScimIdentity = new ResolvedIdentity("alice-plain", null, null, null, TenantA, "nonscim-realm", []);
        await roleResolver.ResolveRolesAsync(nonScimIdentity);
        (await cache.GetAsync("nonscim-realm", "role:alice-plain")).ShouldNotBeNull();
        cache.Invalidate("nonscim-realm", "alice-plain");
        (await cache.GetAsync("nonscim-realm", "role:alice-plain")).ShouldBeNull();

        // Cache accommodates max-length (256-char) userKeys with role/SCIM prefixes and invalidates userKeys literally prefixed with "role:".
        var maxUserKey = new string('u', 256);
        await cache.SetAsync("nonscim-realm", $"role:{maxUserKey}", resolvedRoles);
        await cache.SetAsync("scim-realm", $"role:e0:{maxUserKey}", resolvedRoles);
        await cache.SetAsync("scim-realm", $"scim:e0:{maxUserKey}", resolvedRoles);
        (await cache.GetAsync("nonscim-realm", $"role:{maxUserKey}")).ShouldNotBeNull();
        (await cache.GetAsync("scim-realm", $"role:e0:{maxUserKey}")).ShouldNotBeNull();
        (await cache.GetAsync("scim-realm", $"scim:e0:{maxUserKey}")).ShouldNotBeNull();

        var overlongPrefixedKey = $"role:e1:{new string('k', IdentityGroupsChangedChannel.MaxKeyLength + 1)}";
        await cache.SetAsync("scim-realm", overlongPrefixedKey, resolvedRoles);
        (await cache.GetAsync("scim-realm", overlongPrefixedKey)).ShouldBeNull();

        await roleResolver.ResolveRolesAsync(new ResolvedIdentity("role:admin", null, null, null, TenantA, "nonscim-realm", []));
        (await cache.GetAsync("nonscim-realm", "role:role:admin")).ShouldNotBeNull();
        cache.Invalidate("nonscim-realm", "role:admin");
        (await cache.GetAsync("nonscim-realm", "role:role:admin")).ShouldBeNull();
    }

    private static async Task VerifyScimAdditionInvalidationAsync()
    {
        var store = new InMemoryScimCacheAndEpochStore();
        var service = new ScimService(store, new NoOpDeprovisioning());
        const string realmId = "workforce-entra";

        await service.CreateGroupAsync(
            TenantA,
            realmId,
            new ScimGroup("g1", "ext1", "G1", [new ScimGroupMember("u1", "U1", ScimMemberTypes.User)], null, null, ObjectSid: "S-1-5-21-1000-2000-3000-9001"));
        (await store.GetRealmSyncStateAsync(TenantA, realmId))!.DeprovisionEpoch.ShouldBe(1L);

        var patchAdd = JsonSerializer.SerializeToElement(new[] { new { value = "u2", type = "User" } });
        await service.PatchGroupAsync(
            TenantA,
            realmId,
            "g1",
            new ScimPatchRequest(["urn:ietf:params:scim:api:messages:2.0:PatchOp"], [new ScimPatchOperation("add", "members", patchAdd)]));
        (await store.GetRealmSyncStateAsync(TenantA, realmId))!.DeprovisionEpoch.ShouldBe(2L);

        await service.UpdateGroupAsync(
            TenantA,
            realmId,
            "g1",
            new ScimGroup("g1", "ext1", "G1", [new ScimGroupMember("u1", "U1", ScimMemberTypes.User), new ScimGroupMember("u2", "U2", ScimMemberTypes.User), new ScimGroupMember("u3", "U3", ScimMemberTypes.User)], null, null, ObjectSid: "S-1-5-21-1000-2000-3000-9001"));
        (await store.GetRealmSyncStateAsync(TenantA, realmId))!.DeprovisionEpoch.ShouldBe(3L);
    }

    private static void VerifyFailClosedRevocationAuthority()
    {
        var fromNull = ConsentRevocationAuthority.FromPrincipal(null);
        fromNull.CanAccessTenant(TenantA).ShouldBeFalse();
        fromNull.CanAccessTenant(new TenantId("tenant-b")).ShouldBeFalse();
        fromNull.AllowsAdminForTenant(TenantA).ShouldBeFalse();

        var tenantlessAuth = ConsentRevocationAuthority.FromPrincipal(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1000-1"), new Claim(ClaimTypes.Role, "Consumer")], "Bearer")));
        tenantlessAuth.CanAccessTenant(TenantA).ShouldBeFalse();
        tenantlessAuth.AllowsAdminForTenant(TenantA).ShouldBeFalse();
    }

    private static DataTable BuildConsentSubjectRows(params (string GranteeSid, Guid Id)[] rows)
    {
        var dt = new DataTable();
        dt.Columns.Add("id", typeof(string));
        dt.Columns.Add("table_id", typeof(string));
        dt.Columns.Add("consent_request_id", typeof(string));
        dt.Columns.Add("effect", typeof(string));
        dt.Columns.Add("grantee_type", typeof(string));
        dt.Columns.Add("grantee_sid", typeof(string));
        dt.Columns.Add("valid_from", typeof(string));
        dt.Columns.Add("valid_to", typeof(string));
        dt.Columns.Add("is_revoked", typeof(int));
        dt.Columns.Add("revoked_by_sid", typeof(string));
        dt.Columns.Add("revoked_at", typeof(string));
        dt.Columns.Add("revoke_reason", typeof(string));
        dt.Columns.Add("tenant_id", typeof(string));
        var now = DateTimeOffset.UtcNow;
        var tableId = Guid.NewGuid().ToString();
        foreach (var (sid, id) in rows)
        {
            dt.Rows.Add(id.ToString(), tableId, DBNull.Value, "Allow", "User", sid, now.AddDays(-1).ToString("O"), now.AddDays(5).ToString("O"), 0, DBNull.Value, DBNull.Value, DBNull.Value, TenantA.Value);
        }

        return dt;
    }

    private sealed class RecordingRoleCache : IRoleResolverCache
    {
        public List<(string RealmId, string UserKey)> KeysSeen { get; } = [];
        private readonly RoleResolverCache _inner = new();

        public ValueTask<RoleResolution?> GetAsync(string realmId, string userKey, CancellationToken ct = default)
        {
            KeysSeen.Add((realmId, userKey));
            return _inner.GetAsync(realmId, userKey, ct);
        }

        public ValueTask SetAsync(string realmId, string userKey, RoleResolution resolution, CancellationToken ct = default)
        {
            KeysSeen.Add((realmId, userKey));
            return _inner.SetAsync(realmId, userKey, resolution, ct);
        }

        public void Invalidate(string realmId, string userKey) => _inner.Invalidate(realmId, userKey);
        public void InvalidateAll() => _inner.InvalidateAll();
    }

    private sealed class StaticRoleProvider(string providerId, params string[] groupSids) : IRoleProvider
    {
        public string ProviderId => providerId;

        public Task<ProviderRoleResult> GetRolesAsync(ResolvedIdentity identity, IdentityRealm realm, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderRoleResult(new HashSet<string>(groupSids), new HashSet<string>(), new Dictionary<string, string>()));
    }

    private sealed class RecordingApprovalRepository : IConsentApprovalRepository
    {
        public List<ConsentRequest> Requests { get; init; } = [];
        public TenantId? LastRequesterTenant { get; private set; }
        public TenantId? LastApproverTenant { get; private set; }

        public Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, TenantId tenantId, string? status = null, CancellationToken ct = default)
        {
            LastRequesterTenant = tenantId;
            return Task.FromResult<IReadOnlyList<ConsentRequest>>(Requests.Where(r => r.TenantId == tenantId && r.RequesterSid == requesterSid).ToList());
        }

        public Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, TenantId tenantId, CancellationToken ct = default)
        {
            LastApproverTenant = tenantId;
            return Task.FromResult<IReadOnlyList<ConsentRequest>>(Requests.Where(r => r.TenantId == tenantId).ToList());
        }

        public Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoOpDeprovisioning : IIdentityDeprovisioningService
    {
        public Task DeprovisionAndLockSubjectAsync(TenantId tenantId, string realmId, string userKey, string reason, string source, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InMemoryScimCacheAndEpochStore(string? effectiveUserKey = null, string? effectiveGroupSid = null) : IIdentityStoreRepository
    {
        private readonly Dictionary<(string Tenant, string Realm, string Id), ScimGroup> _groups = [];
        private readonly HashSet<(string Tenant, string Realm, string GroupId, string UserKey)> _members = [];
        private long _epoch;

        public Task<ScimUser?> GetUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.FromResult<ScimUser?>(null);
        public Task<ScimUser?> GetUserByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) => Task.FromResult<ScimUser?>(null);
        public Task<ScimUser?> GetUserByUserNameAsync(TenantId tenantId, string realmId, string userName, CancellationToken ct = default) => Task.FromResult<ScimUser?>(null);
        public Task<ScimUser?> GetUserByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) => Task.FromResult<ScimUser?>(null);
        public Task<ScimUser?> GetUserByImmutableIdAsync(TenantId tenantId, string realmId, string onPremisesImmutableId, CancellationToken ct = default) => Task.FromResult<ScimUser?>(null);
        public Task<ScimUser?> GetUserByEntraObjectIdAsync(TenantId tenantId, string realmId, string entraObjectId, CancellationToken ct = default) => Task.FromResult<ScimUser?>(null);
        public Task<ScimUser?> GetUserByUpnAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default) => Task.FromResult<ScimUser?>(null);
        public Task<IReadOnlyList<ScimUser>> GetUsersByEntraObjectIdOrExternalIdAsync(TenantId tenantId, string realmId, string identifier, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScimUser>>(
                effectiveUserKey is not null && string.Equals(identifier, effectiveUserKey, StringComparison.OrdinalIgnoreCase)
                    ? [new ScimUser(effectiveUserKey, effectiveUserKey, effectiveUserKey, null, null, true, null, null, null)]
                    : []);
        public Task<IReadOnlyList<ScimUser>> GetUsersByUpnOrUserNameAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimUser>>([]);
        public Task CreateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertUserAsync(ScimUser user, TenantId tenantId, string realmId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ScimUser>> ListUsersAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimUser>>([]);
        public Task<ScimGroup?> GetGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => Task.FromResult(_groups.GetValueOrDefault((tenantId.Value, realmId, groupId)));
        public Task<ScimGroup?> GetGroupByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) => Task.FromResult<ScimGroup?>(null);
        public Task<ScimGroup?> GetGroupByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) => Task.FromResult<ScimGroup?>(null);
        public Task<EffectiveScimGroupsResult> GetEffectiveGroupSidsForUserAsync(TenantId tenantId, string realmId, string requestedUserKey, int maxDepth = 32, CancellationToken ct = default)
        {
            if (effectiveUserKey is null || effectiveGroupSid is null || !string.Equals(requestedUserKey, effectiveUserKey, StringComparison.Ordinal))
            {
                return Task.FromResult(new EffectiveScimGroupsResult(new HashSet<string>(), MaxDepthExceeded: false));
            }

            if (string.Equals(realmId, "oidc-scim", StringComparison.Ordinal))
            {
                return Task.FromResult(new EffectiveScimGroupsResult(
                    new HashSet<string> { "realm:oidc-scim:grp-7777" },
                    MaxDepthExceeded: false,
                    HasNonCanonicalSid: false,
                    GroupIds: new HashSet<string> { "grp-7777" }));
            }

            return Task.FromResult(new EffectiveScimGroupsResult(
                new HashSet<string> { effectiveGroupSid },
                MaxDepthExceeded: false));
        }
        public Task UpsertGroupAsync(ScimGroup group, TenantId tenantId, string realmId, CancellationToken ct = default) => CreateGroupAsync(tenantId, realmId, group, ct);
        public Task CreateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default) { _groups[(tenantId.Value, realmId, group.GroupId)] = group; return Task.CompletedTask; }
        public Task UpdateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default) => CreateGroupAsync(tenantId, realmId, group, ct);
        public Task<IReadOnlyList<string>> DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<ScimGroup>> ListGroupsAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimGroup>>([]);
        public Task AddGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default) { _members.Add((tenantId.Value, realmId, groupId, userKey)); return Task.CompletedTask; }
        public Task RemoveGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default) { _members.Remove((tenantId.Value, realmId, groupId, userKey)); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> GetGroupMembershipsForUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> GetGroupMembersAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(_members.Where(m => m.GroupId == groupId).Select(m => m.UserKey).ToList());
        public Task AddGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetChildGroupIdsAsync(TenantId tenantId, string realmId, string parentGroupId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<ScimGroupNestingEdge>> ListGroupNestingEdgesAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimGroupNestingEdge>>([]);
        public Task<ScimRealmSyncState?> GetRealmSyncStateAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => Task.FromResult<ScimRealmSyncState?>(new ScimRealmSyncState(tenantId, realmId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, _epoch));
        public Task UpsertRealmSyncStateAsync(TenantId tenantId, string realmId, DateTimeOffset lastContactAt, DateTimeOffset? lastWriteAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> IncrementRealmDeprovisionEpochAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => Task.FromResult(++_epoch);
        public Task TouchRealmSyncContactAsync(TenantId tenantId, string realmId, DateTimeOffset contactedAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task TouchRealmSyncWriteAsync(TenantId tenantId, string realmId, DateTimeOffset writtenAt, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddLockAsync(ScimLock lockEntry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> IsSubjectLockedAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<ScimLock>> GetLocksAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimLock>>([]);
        public Task<ScimLockWindow> CountLocksSinceAsync(TenantId tenantId, string realmId, IReadOnlyList<string> sources, DateTimeOffset sinceUtc, int cap, CancellationToken ct = default) => Task.FromResult(new ScimLockWindow(0, null));
        public Task RemoveLockAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
    }
}
