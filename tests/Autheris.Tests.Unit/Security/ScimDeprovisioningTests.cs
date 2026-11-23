namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
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
using Autheris.Domain.Interfaces;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;
using AuthenticationOptions = Autheris.Domain.Options.AuthenticationOptions;

[Trait("Category", "Security")]
public sealed class ScimDeprovisioningTests
{
    private const string SidA = "S-1-5-21-100-200-300-2101";
    private const string SidB = "S-1-5-21-100-200-300-2102";
    private const string OidA = "11111111-2222-3333-4444-555555555555";
    private static readonly TenantId Tenant = new("tenant-1");

    private static IdentityRealm ScimRealm() => new(
        "realm-1",
        IdentityRealmKind.EntraWorkforce,
        TenantBindingSpecification.Fixed(Tenant),
        RealmGroupSource.Token,
        RealmProvisioningMode.Scim,
        issuers: ["https://issuer.example.org"]);

    private static ClaimsPrincipal Principal() =>
        new(new ClaimsIdentity([new Claim("oid", OidA)], "Bearer"));

    [Fact]
    public async Task Delete_user_removes_memberships_and_bumps_epoch()
    {
        var store = new SharedDeprovisionStore();
        store.SeedUser("u-1", OidA, active: true);
        store.SeedGroup("g-1", SidA);
        store.Memberships.Add(("g-1", "u-1"));
        var deprov = new IdentityDeprovisioningService(store, Autheris.Tests.Unit.TestSupport.NoOpGrantRevocation.Instance);
        var svc = new ScimService(store, deprov);

        var deleted = await svc.DeleteUserAsync(Tenant, "realm-1", "u-1");

        deleted.ShouldBeTrue();
        store.Users.ContainsKey("u-1").ShouldBeFalse();
        store.Memberships.ShouldNotContain(("g-1", "u-1"));
        store.DeprovisionEpoch.ShouldBeGreaterThanOrEqualTo(1L);
    }

    [Fact]
    public async Task Active_false_removes_memberships()
    {
        var store = new SharedDeprovisionStore();
        store.SeedUser("u-1", OidA, active: true);
        store.SeedGroup("g-1", SidA);
        store.Memberships.Add(("g-1", "u-1"));
        var deprov = new IdentityDeprovisioningService(store, Autheris.Tests.Unit.TestSupport.NoOpGrantRevocation.Instance);
        var svc = new ScimService(store, deprov);

        var patch = new ScimPatchRequest([ScimSchemaUris.PatchOp], [new ScimPatchOperation("replace", "active", false)]);
        var updated = await svc.PatchUserAsync(Tenant, "realm-1", "u-1", patch);

        updated.ShouldNotBeNull();
        updated!.Active.ShouldBeFalse();
        store.Memberships.ShouldNotContain(("g-1", "u-1"));
        store.DeprovisionEpoch.ShouldBeGreaterThanOrEqualTo(1L);
    }

    [Fact]
    public async Task Member_remove_invalidates_other_node()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FixedTimeProvider(now);
        var store = new SharedDeprovisionStore { LastContactAt = now };
        store.SeedUser("u-1", OidA, active: true);
        store.SeedGroup("g-1", SidA);
        store.SeedGroup("g-2", SidB);
        store.Memberships.Add(("g-1", "u-1"));
        store.Memberships.Add(("g-2", "u-1"));

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

        var before = await node2Resolver.ResolveAsync(Principal(), ScimRealm(), Tenant, "Bearer", CancellationToken.None);
        before.GroupSids.Select(s => s.Value).OrderBy(x => x, StringComparer.Ordinal).ToArray().ShouldBe([SidA, SidB]);
        store.EffectiveQueryCount.ShouldBe(1);

        var cached = await node2Resolver.ResolveAsync(Principal(), ScimRealm(), Tenant, "Bearer", CancellationToken.None);
        cached.GroupSids.Count.ShouldBe(2);
        store.EffectiveQueryCount.ShouldBe(1);

        var node1Scim = new ScimService(store, new IdentityDeprovisioningService(store, Autheris.Tests.Unit.TestSupport.NoOpGrantRevocation.Instance, roleResolverCache: node1Cache), roleCache: node1Cache, timeProvider: time);
        var patch = new ScimPatchRequest([ScimSchemaUris.PatchOp], [new ScimPatchOperation("remove", "members[value eq \"u-1\"]", null)]);
        await node1Scim.PatchGroupAsync(Tenant, "realm-1", "g-2", patch);

        var after = await node2Resolver.ResolveAsync(Principal(), ScimRealm(), Tenant, "Bearer", CancellationToken.None);
        store.EffectiveQueryCount.ShouldBe(2);
        after.GroupSids.Select(s => s.Value).ToArray().ShouldBe([SidA]);
    }

    [Fact]
    public async Task Response_after_commit_and_epoch()
    {
        var store = new SharedDeprovisionStore();
        store.SeedUser("u-1", OidA, active: true);
        store.SeedGroup("g-1", SidA);
        store.Memberships.Add(("g-1", "u-1"));
        var svc = new ScimService(store, new IdentityDeprovisioningService(store, Autheris.Tests.Unit.TestSupport.NoOpGrantRevocation.Instance));

        await svc.DeleteUserAsync(Tenant, "realm-1", "u-1");

        var deleteIndex = store.OperationLog.IndexOf("DELETE_USER");
        var epochIndex = store.OperationLog.LastIndexOf("INCREMENT_EPOCH");
        deleteIndex.ShouldBeGreaterThanOrEqualTo(0);
        epochIndex.ShouldBeGreaterThan(deleteIndex);
    }

    [Fact]
    public async Task Deactivate_invalidates_caches()
    {
        var t0 = new DateTimeOffset(2026, 11, 22, 13, 0, 0, TimeSpan.Zero);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(t0);
        var store = new SharedDeprovisionStore { LastContactAt = t0 };
        store.SeedUser("u-1", OidA, active: true);
        store.SeedGroup("g-1", SidA);
        store.Memberships.Add(("g-1", "u-1"));

        using var notifier = new Autheris.Infrastructure.Cache.RedisGovernanceChangeChannel(timeProvider: time);
        var roleCache = new RoleResolverCache(timeProvider: time, changeNotifier: notifier);
        using var memoryCache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 10_000_000 });
        var consentCache = new Autheris.Infrastructure.Cache.ConsentCacheService(
            memoryCache,
            new Autheris.Tests.Common.FakeEpochValidationService(),
            Autheris.Tests.Unit.TestDoubles.StubEventBus.Instance,
            timeProvider: time,
            changeNotifier: notifier);
        using var visibilityCache = new Autheris.Application.Catalog.Search.CatalogVisibilityLruCache(
            ttl: TimeSpan.FromMinutes(5),
            timeProvider: time,
            changeNotifier: notifier);

        var table = new TableIdentifier("sales", "dbo", "orders");
        await consentCache.SetCachedDecisionAsync(
            Tenant,
            new Sid("u-1"),
            table,
            Autheris.Domain.Interfaces.TableAccessDecision.Allowed(table, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            TimeSpan.FromMinutes(5));
        var visKey = new Autheris.Application.Catalog.Search.CatalogVisibilityCacheKey("u-1", null, 1, 1, 1, Tenant.Value);
        visibilityCache.Set(visKey, new Autheris.Application.Catalog.Search.CatalogVisibleBitSet(4));

        var options = Options.Create(new GatewayOptions { Authentication = new AuthenticationOptions() });
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

        var initial = await resolver.ResolveAsync(Principal(), ScimRealm(), Tenant, "Bearer", CancellationToken.None);
        initial.Status.ShouldBe(GroupResolutionStatus.Complete);
        initial.GroupSids.ShouldContain(new Sid(SidA));
        (await consentCache.GetCachedDecisionAsync(Tenant, new Sid("u-1"), table)).ShouldNotBeNull();
        visibilityCache.TryGet(visKey, out _).ShouldBeTrue();

        var deprov = new IdentityDeprovisioningService(
            store, Autheris.Tests.Unit.TestSupport.NoOpGrantRevocation.Instance,
            roleResolverCache: roleCache,
            governanceChangeNotifier: notifier,
            timeProvider: time);
        var svc = new ScimService(
            store,
            deprov,
            roleCache: roleCache,
            timeProvider: time,
            governanceChangeNotifier: notifier);

        var patch = new ScimPatchRequest([ScimSchemaUris.PatchOp], [new ScimPatchOperation("replace", "active", false)]);
        var result = await svc.PatchUserAsync(Tenant, "realm-1", "u-1", patch);
        result.ShouldNotBeNull();
        result!.Active.ShouldBeFalse();

        (await consentCache.GetCachedDecisionAsync(Tenant, new Sid("u-1"), table)).ShouldBeNull();
        visibilityCache.TryGet(visKey, out _).ShouldBeFalse();
        (await roleCache.GetAsync("realm-1", $"role:e{store.DeprovisionEpoch}:{OidA}")).ShouldBeNull();

        var after = await resolver.ResolveAsync(Principal(), ScimRealm(), Tenant, "Bearer", CancellationToken.None);
        after.Status.ShouldBe(GroupResolutionStatus.Degraded);
        after.GroupSids.ShouldBeEmpty();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    internal sealed class SharedDeprovisionStore : IIdentityStoreRepository
    {
        public Dictionary<string, ScimUser> Users { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ScimGroup> Groups { get; } = new(StringComparer.Ordinal);
        public HashSet<(string GroupId, string UserKey)> Memberships { get; } = [];
        public HashSet<(string ParentGroupId, string ChildGroupId)> Nesting { get; } = [];
        public HashSet<string> Locks { get; } = new(StringComparer.Ordinal);
        public long DeprovisionEpoch { get; set; }
        public DateTimeOffset? LastContactAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastWriteAt { get; set; }
        public int EffectiveQueryCount { get; private set; }
        public List<string> OperationLog { get; } = [];

        public void SeedUser(string id, string oid, bool active) =>
            Users[id] = new ScimUser(id, oid, id, id, null, active, active ? null : DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, EntraObjectId: oid);

        public void SeedGroup(string id, string sid) =>
            Groups[id] = new ScimGroup(id, id, id, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ObjectSid: sid);

        public Task<EffectiveScimGroupsResult> GetEffectiveGroupSidsForUserAsync(TenantId tenantId, string realmId, string userKey, int maxDepth = 32, CancellationToken ct = default)
        {
            EffectiveQueryCount++;
            var sids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (gid, uk) in Memberships)
            {
                if (uk == userKey && Groups.TryGetValue(gid, out var grp) && grp.ObjectSid is { } sid)
                {
                    sids.Add(sid);
                }
            }
            return Task.FromResult(new EffectiveScimGroupsResult(sids, false));
        }

        public Task<ScimRealmSyncState?> GetRealmSyncStateAsync(TenantId tenantId, string realmId, CancellationToken ct = default) =>
            Task.FromResult<ScimRealmSyncState?>(new ScimRealmSyncState(tenantId, realmId, LastContactAt, LastWriteAt, DeprovisionEpoch));

        public Task UpsertRealmSyncStateAsync(TenantId tenantId, string realmId, DateTimeOffset lastContactAt, DateTimeOffset? lastWriteAt, CancellationToken ct = default)
        {
            LastContactAt = lastContactAt;
            if (lastWriteAt.HasValue)
            {
                LastWriteAt = lastWriteAt;
            }
            return Task.CompletedTask;
        }

        public Task<long> IncrementRealmDeprovisionEpochAsync(TenantId tenantId, string realmId, CancellationToken ct = default)
        {
            DeprovisionEpoch++;
            OperationLog.Add("INCREMENT_EPOCH");
            return Task.FromResult(DeprovisionEpoch);
        }

        public Task DeactivateUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default)
        {
            OperationLog.Add("DEACTIVATE_USER");
            Memberships.RemoveWhere(m => m.UserKey == userKey);
            if (Users.TryGetValue(userKey, out var u))
            {
                Users[userKey] = u with { Active = false, DeactivatedAt = DateTimeOffset.UtcNow };
            }
            return Task.CompletedTask;
        }

        public Task DeleteUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default)
        {
            OperationLog.Add("DELETE_USER");
            Memberships.RemoveWhere(m => m.UserKey == userKey);
            Users.Remove(userKey);
            DeprovisionEpoch++;
            return Task.CompletedTask;
        }

        public Task UpdateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default)
        {
            Users[user.UserKey] = user;
            if (!user.Active)
            {
                Memberships.RemoveWhere(m => m.UserKey == user.UserKey);
            }
            return Task.CompletedTask;
        }

        public Task RemoveGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default)
        {
            Memberships.Remove((groupId, userKey));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default)
        {
            var removed = Memberships.Where(m => m.GroupId == groupId).Select(m => m.UserKey).ToList();
            Memberships.RemoveWhere(m => m.GroupId == groupId);
            Nesting.RemoveWhere(n => n.ParentGroupId == groupId || n.ChildGroupId == groupId);
            Groups.Remove(groupId);
            return Task.FromResult<IReadOnlyList<string>>(removed);
        }

        public Task AddLockAsync(ScimLock lockEntry, CancellationToken ct = default) { Locks.Add(lockEntry.UserKey); return Task.CompletedTask; }
        public Task<bool> IsSubjectLockedAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.FromResult(Locks.Contains(userKey));
        public Task<ScimUser?> GetUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.FromResult(Users.GetValueOrDefault(userKey));
        public Task<ScimUser?> GetUserByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) => Task.FromResult(Users.Values.FirstOrDefault(u => u.ExternalId == externalId));
        public Task<ScimUser?> GetUserByUserNameAsync(TenantId tenantId, string realmId, string userName, CancellationToken ct = default) => Task.FromResult(Users.Values.FirstOrDefault(u => u.UserName == userName));
        public Task<ScimUser?> GetUserByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) => Task.FromResult(Users.Values.FirstOrDefault(u => u.ObjectSid == objectSid));
        public Task<ScimUser?> GetUserByImmutableIdAsync(TenantId tenantId, string realmId, string onPremisesImmutableId, CancellationToken ct = default) => Task.FromResult(Users.Values.FirstOrDefault(u => u.OnPremisesImmutableId == onPremisesImmutableId));
        public Task<ScimUser?> GetUserByEntraObjectIdAsync(TenantId tenantId, string realmId, string entraObjectId, CancellationToken ct = default) => Task.FromResult(Users.Values.FirstOrDefault(u => u.EntraObjectId == entraObjectId));
        public Task<ScimUser?> GetUserByUpnAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default) => Task.FromResult(Users.Values.FirstOrDefault(u => u.UserPrincipalName == userPrincipalName));
        public Task<IReadOnlyList<ScimUser>> GetUsersByEntraObjectIdOrExternalIdAsync(TenantId tenantId, string realmId, string identifier, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimUser>>(Users.Values.Where(u => u.EntraObjectId == identifier || u.ExternalId == identifier).ToList());
        public Task<IReadOnlyList<ScimUser>> GetUsersByUpnOrUserNameAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimUser>>(Users.Values.Where(u => string.Equals(u.UserPrincipalName, userPrincipalName, StringComparison.OrdinalIgnoreCase) || string.Equals(u.UserName, userPrincipalName, StringComparison.OrdinalIgnoreCase)).ToList());
        public Task UpsertUserAsync(ScimUser user, TenantId tenantId, string realmId, CancellationToken ct = default) { Users[user.UserKey] = user; return Task.CompletedTask; }
        public Task CreateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default) { Users[user.UserKey] = user; return Task.CompletedTask; }
        public Task<IReadOnlyList<ScimUser>> ListUsersAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimUser>>(Users.Values.ToList());
        public Task<ScimGroup?> GetGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => Task.FromResult(Groups.GetValueOrDefault(groupId));
        public Task<ScimGroup?> GetGroupByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) => Task.FromResult(Groups.Values.FirstOrDefault(g => g.ExternalId == externalId));
        public Task<ScimGroup?> GetGroupByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) => Task.FromResult(Groups.Values.FirstOrDefault(g => g.ObjectSid == objectSid));
        public Task UpsertGroupAsync(ScimGroup group, TenantId tenantId, string realmId, CancellationToken ct = default) { Groups[group.GroupId] = group; return Task.CompletedTask; }
        public Task CreateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default) { Groups[group.GroupId] = group; return Task.CompletedTask; }
        public Task UpdateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default) { Groups[group.GroupId] = group; return Task.CompletedTask; }
        public Task<IReadOnlyList<ScimGroup>> ListGroupsAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimGroup>>(Groups.Values.ToList());
        public Task AddGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default) { Memberships.Add((groupId, userKey)); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> GetGroupMembershipsForUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Memberships.Where(m => m.UserKey == userKey).Select(m => m.GroupId).ToList());
        public Task<IReadOnlyList<string>> GetGroupMembersAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Memberships.Where(m => m.GroupId == groupId).Select(m => m.UserKey).ToList());
        public Task AddGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default) { Nesting.Add((parentGroupId, childGroupId)); return Task.CompletedTask; }
        public Task RemoveGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default) { Nesting.Remove((parentGroupId, childGroupId)); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> GetChildGroupIdsAsync(TenantId tenantId, string realmId, string parentGroupId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Nesting.Where(n => n.ParentGroupId == parentGroupId).Select(n => n.ChildGroupId).ToList());
        public Task<IReadOnlyList<ScimGroupNestingEdge>> ListGroupNestingEdgesAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimGroupNestingEdge>>(Nesting.Select(n => new ScimGroupNestingEdge(n.ParentGroupId, n.ChildGroupId)).ToList());
        public Task<IReadOnlyList<ScimLock>> GetLocksAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimLock>>([]);
        public Task<ScimLockWindow> CountLocksSinceAsync(TenantId tenantId, string realmId, IReadOnlyList<string> sources, DateTimeOffset sinceUtc, int cap, CancellationToken ct = default) => Task.FromResult(new ScimLockWindow(0, null));
        public Task RemoveLockAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) { Locks.Remove(userKey); return Task.CompletedTask; }
    }
}
