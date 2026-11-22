namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Rights;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Identity.Providers;
using Autheris.Application.Identity.Services;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Application.Services;
using Autheris.Application.Tenancy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Security;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using AuthenticationOptions = Autheris.Domain.Options.AuthenticationOptions;

[Trait("Category", "Security")]
public sealed class GroupMembershipStalenessTests
{
    private const string RealmId = "workforce-entra";
    private const string EntraTenantGuid = "11111111-1111-1111-1111-111111111111";
    private const string TokenIssuer = $"https://login.microsoftonline.com/{EntraTenantGuid}/v2.0";
    private const string SidGroupA = "S-1-5-21-100-200-300-7101";
    private const string SidGroupB = "S-1-5-21-100-200-300-7102";
    private const string UserOid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string UserSid = "S-1-5-21-100-200-300-1001";
    private static readonly TenantId Tenant = new("tenant-a");

    [Fact]
    public async Task Scim_membership_change_invalidates_group_and_consent_cache()
    {
        var t0 = new DateTimeOffset(2026, 11, 22, 16, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(t0);
        var store = new ScimDeprovisioningTests.SharedDeprovisionStore { LastContactAt = t0 };
        store.SeedUser("u-1", UserOid, active: true);
        store.SeedGroup("g-1", SidGroupA);
        store.SeedGroup("g-2", SidGroupB);
        store.Memberships.Add(("g-1", "u-1"));
        store.Memberships.Add(("g-2", "u-1"));

        using var changeNotifier = new RedisGovernanceChangeChannel(timeProvider: time);
        var roleCache = new RoleResolverCache(timeProvider: time, changeNotifier: changeNotifier);
        using var memoryCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10_000_000 });
        var epochService = new FakeEpochValidationService();
        var consentCache = new ConsentCacheService(
            memoryCache,
            epochService,
            StubEventBus.Instance,
            timeProvider: time,
            changeNotifier: changeNotifier);
        var profileRepo = new CountingAccessProfileRepository();
        using var profileCache = new AccessProfileCache(
            profileRepo,
            new StubClusterStateProvider(time),
            memoryCache,
            timeProvider: time,
            changeNotifier: changeNotifier);
        using var visibilityCache = new CatalogVisibilityLruCache(
            ttl: TimeSpan.FromMinutes(10),
            timeProvider: time,
            changeNotifier: changeNotifier);

        var options = Options.Create(new GatewayOptions { Authentication = new AuthenticationOptions { GroupCacheTtlMinutes = 5 } });
        var resolver = new GroupSidResolver(
            new RoleResolver([new ScimStoreRoleProvider(store)], roleCache, null, options, store, time),
            [],
            options,
            timeProvider: time,
            scimCorrelator: new ScimIdentityCorrelator(store, store),
            scimGroupStore: store,
            scimProvisioningStore: store,
            syncStateTracker: new ScimSyncStateTracker(store, time),
            roleCache: roleCache);
        var scimRealm = new IdentityRealm(
            RealmId,
            IdentityRealmKind.EntraWorkforce,
            TenantBindingSpecification.Fixed(Tenant),
            RealmGroupSource.Token,
            RealmProvisioningMode.Scim,
            issuers: [TokenIssuer]);
        var rawPrincipal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("oid", UserOid)], "Bearer"));

        // Populate group, consent, unmask (access profile), and visibility caches.
        var initialGroups = await resolver.ResolveAsync(rawPrincipal, scimRealm, Tenant, "Bearer", CancellationToken.None);
        initialGroups.GroupSids.Select(s => s.Value).ShouldContain(SidGroupA);
        initialGroups.GroupSids.Select(s => s.Value).ShouldContain(SidGroupB);
        store.EffectiveQueryCount.ShouldBe(1);

        var table = new TableIdentifier("sales", "dbo", "customers");
        await consentCache.SetCachedDecisionAsync(
            Tenant,
            new Sid("u-1"),
            table,
            TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            TimeSpan.FromMinutes(5));
        (await consentCache.GetCachedDecisionAsync(Tenant, new Sid("u-1"), table)).ShouldNotBeNull();

        await profileCache.GetProfilesAsync(Tenant, "u-1");
        await profileCache.GetProfilesAsync(Tenant, "u-1");
        profileRepo.ReadCount.ShouldBe(1);

        var visKey = new CatalogVisibilityCacheKey("u-1", null, 1, 1, 1, Tenant.Value);
        visibilityCache.Set(visKey, new CatalogVisibleBitSet(8));
        visibilityCache.TryGet(visKey, out _).ShouldBeTrue();

        // Remove user from g-2 via SCIM PatchGroupAsync without advancing FakeTimeProvider.
        var scim = new ScimService(
            store,
            new IdentityDeprovisioningService(store, roleResolverCache: roleCache, governanceChangeNotifier: changeNotifier, timeProvider: time),
            roleCache: roleCache,
            timeProvider: time,
            governanceChangeNotifier: changeNotifier);
        var patch = new ScimPatchRequest(
            [ScimSchemaUris.PatchOp],
            [new ScimPatchOperation("remove", "members[value eq \"u-1\"]", null)]);
        await scim.PatchGroupAsync(Tenant, RealmId, "g-2", patch);

        // All dependent caches must invalidate immediately upon SCIM change.
        (await consentCache.GetCachedDecisionAsync(Tenant, new Sid("u-1"), table)).ShouldBeNull();
        visibilityCache.TryGet(visKey, out _).ShouldBeFalse();
        await profileCache.GetProfilesAsync(Tenant, "u-1");
        profileRepo.ReadCount.ShouldBe(2);

        var refreshedGroups = await resolver.ResolveAsync(rawPrincipal, scimRealm, Tenant, "Bearer", CancellationToken.None);
        store.EffectiveQueryCount.ShouldBe(2);
        refreshedGroups.GroupSids.Select(s => s.Value).ShouldBe([SidGroupA]);
    }

    [Fact]
    public async Task Token_groups_bounded_by_ttl()
    {
        var t0 = new DateTimeOffset(2026, 11, 22, 16, 30, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(t0);
        var gatewayOptions = new GatewayOptions
        {
            Authentication = new AuthenticationOptions { GroupCacheTtlMinutes = 5 }
        };
        var opts = Options.Create(gatewayOptions);

        var realm = new IdentityRealm(
            RealmId,
            IdentityRealmKind.EntraWorkforce,
            TenantBindingSpecification.Fixed(Tenant),
            RealmGroupSource.Token,
            RealmProvisioningMode.None,
            issuers: [TokenIssuer]);
        var registry = new FakeIdentityRealmRegistry(realm);

        var groupResolver = new GroupSidResolver(null, [], opts, timeProvider: time);
        var pipeline = new IdentityPipeline(
            registry,
            groupResolver,
            options: opts,
            timeProvider: time);

        // Even with a 60-minute JWT `exp`, token-embedded groups are bounded to GroupCacheTtlMinutes (5 minutes).
        var longExp = t0.AddMinutes(60).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var longLivedRaw = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, UserSid),
            new Claim("oid", UserOid),
            new Claim("tid", EntraTenantGuid),
            new Claim("iss", TokenIssuer),
            new Claim("groups", SidGroupA),
            new Claim("exp", longExp)
        ], "Bearer"));

        var resolution = await groupResolver.ResolveAsync(longLivedRaw, realm, Tenant, "Bearer", CancellationToken.None);
        resolution.Status.ShouldBe(GroupResolutionStatus.Complete);
        resolution.GroupSids.ShouldContain(new Sid(SidGroupA));
        resolution.ExpiresAt.ShouldBe(t0.AddMinutes(5));

        // When token `exp` is shorter than GroupCacheTtlMinutes, the tighter `exp` bound wins.
        var shortExp = t0.AddMinutes(2).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var shortRaw = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, UserSid),
            new Claim("oid", UserOid),
            new Claim("tid", EntraTenantGuid),
            new Claim("iss", TokenIssuer),
            new Claim("groups", SidGroupA),
            new Claim("exp", shortExp)
        ], "Bearer"));
        var shortResolution = await groupResolver.ResolveAsync(shortRaw, realm, Tenant, "Bearer", CancellationToken.None);
        shortResolution.ExpiresAt.ShouldBe(t0.AddMinutes(2));

        // Pipeline emits bounded ExpiresAt claim and Complete status.
        var processed = await pipeline.ProcessAsync(longLivedRaw, "Bearer", null, CancellationToken.None);
        GroupResolutionClaims.ReadExpiresAt(processed).ShouldBe(t0.AddMinutes(5));
        GroupResolutionClaims.Read(processed, time.GetUtcNow()).ShouldBe(GroupResolutionStatus.Complete);

        var tableId = new TableIdentifier("sales", "dbo", "orders");
        var meta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "dbo", TableName = "orders", IsActive = true },
            Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "INT" }]
        };
        var consentRepo = new InMemoryConsentRepository(new Consent
        {
            Id = Guid.NewGuid(),
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            TenantId = Tenant,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group,
            GranteeSid = new Sid(SidGroupA),
            ValidFrom = t0.AddDays(-1),
            ValidTo = t0.AddDays(10),
            ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
        });

        var services = new ServiceCollection().AddSingleton<TimeProvider>(time).BuildServiceProvider();
        var consentCacheStub = new InMemoryConsentCacheService();
        var policy = new TableAccessPolicy(
            consentRepo,
            new ConsentResolutionService(),
            consentCacheStub,
            gatewayOptions,
            NullMandatoryRowFilterResolver.Instance,
            timeProvider: time);
        var pdp = new AccessDecisionPoint(
            opts,
            new ConsentResolutionService(),
            consentRepo,
            consentCacheStub,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
            timeProvider: time);

        // At t0 + 4m59s: groups are still within TTL -> SecurityContextFactory, TableAccessPolicy & PDP allow.
        time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        var beforeContext = SecurityContextFactory.CreateFromHttpContext(new DefaultHttpContext { User = processed, RequestServices = services });
        beforeContext.GroupResolutionStatus.ShouldBe(GroupResolutionStatus.Complete);
        beforeContext.GroupSids.ShouldContain(new Sid(SidGroupA));
        beforeContext.GroupsExpireAt.ShouldBe(t0.AddMinutes(5));

        var policyAllowed = await policy.DecideAsync(TableAccessQuery.ForPrincipal(processed, new Sid(UserSid), Tenant, meta), CancellationToken.None);
        policyAllowed.IsAllowed.ShouldBeTrue();
        (await pdp.DecideAsync(new AccessRequest(beforeContext, meta, AccessOperation.Read, AccessTransport.Rest), CancellationToken.None)).IsAllowed.ShouldBeTrue();

        // At t0 + 5m00s (TTL boundary reached): fail closed everywhere -> Degraded + empty groups + PDP/Policy denied.
        time.Advance(TimeSpan.FromSeconds(1));
        var afterFactory = SecurityContextFactory.CreateFromHttpContext(new DefaultHttpContext { User = processed, RequestServices = services });
        afterFactory.GroupResolutionStatus.ShouldBe(GroupResolutionStatus.Degraded);
        afterFactory.GroupSids.ShouldBeEmpty();

        var afterFromPrincipal = SecurityPrincipalContext.FromPrincipal(processed, Tenant, timeProvider: time);
        afterFromPrincipal.GroupResolutionStatus.ShouldBe(GroupResolutionStatus.Degraded);
        afterFromPrincipal.GroupSids.ShouldBeEmpty();

        var reprocessed = await pipeline.ProcessAsync(processed, "Bearer", null, CancellationToken.None);
        GroupResolutionClaims.Read(reprocessed).ShouldBe(GroupResolutionStatus.Degraded);
        reprocessed.GetGroupSids().ShouldBeEmpty();

        var policyDenied = await policy.DecideAsync(TableAccessQuery.ForPrincipal(processed, new Sid(UserSid), Tenant, meta), CancellationToken.None);
        policyDenied.IsAllowed.ShouldBeFalse();

        var pdpDenied = await pdp.DecideAsync(new AccessRequest(afterFactory, meta, AccessOperation.Read, AccessTransport.Rest), CancellationToken.None);
        pdpDenied.IsAllowed.ShouldBeFalse();
        pdpDenied.Reasons.ShouldContain(r => r.Code == "GROUPS_DEGRADED");
    }

    private sealed class CountingAccessProfileRepository : IAccessProfileRepository
    {
        private readonly InMemoryAccessProfileRepository _inner = new();
        public int ReadCount { get; private set; }

        public Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectAsync(TenantId tenantId, string subject, CancellationToken ct = default)
        {
            ReadCount++;
            return _inner.GetProfilesForSubjectAsync(tenantId, subject, ct);
        }

        public Task<IReadOnlyList<AccessProfile>> GetAllProfilesAsync(TenantId tenantId, CancellationToken ct = default) => _inner.GetAllProfilesAsync(tenantId, ct);
        public Task<AccessProfile?> GetProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default) => _inner.GetProfileAsync(tenantId, profileId, ct);
        public Task UpsertProfileAsync(AccessProfile profile, CancellationToken ct = default) => _inner.UpsertProfileAsync(profile, ct);
        public Task<bool> DeleteProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default) => _inner.DeleteProfileAsync(tenantId, profileId, ct);
        public Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectsAsync(TenantId tenantId, IReadOnlyCollection<string> users, IReadOnlyCollection<string> groups, IReadOnlyCollection<string> roles, CancellationToken ct = default)
        {
            ReadCount++;
            return _inner.GetProfilesForSubjectsAsync(tenantId, users, groups, roles, ct);
        }
        public Task<AccessProfileAssignment> AssignProfileAsync(TenantId tenantId, string profileId, AccessProfileAssignment assignment, CancellationToken ct = default) => _inner.AssignProfileAsync(tenantId, profileId, assignment, ct);
        public Task<AccessProfileAssignment> ApproveAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, string approvedBy, CancellationToken ct = default) => _inner.ApproveAssignmentAsync(tenantId, profileId, subjectType, subject, approvedBy, ct);
        public Task<AccessProfileAssignment> RenewAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, DateTimeOffset newExpiresAt, string approvedBy, CancellationToken ct = default) => _inner.RenewAssignmentAsync(tenantId, profileId, subjectType, subject, newExpiresAt, approvedBy, ct);
        public Task<bool> RemoveAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, CancellationToken ct = default) => _inner.RemoveAssignmentAsync(tenantId, profileId, subjectType, subject, ct);
        public Task<IReadOnlyList<DeactivatedProfileAssignment>> GetDeactivatedAssignmentsAsync(CancellationToken ct = default) => _inner.GetDeactivatedAssignmentsAsync(ct);
    }
}
