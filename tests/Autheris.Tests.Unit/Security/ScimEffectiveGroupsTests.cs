namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Identity;
using Autheris.Application.Identity.Caching;
using Autheris.Application.Identity.Providers;
using Autheris.Application.Identity.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;
using AuthenticationOptions = Autheris.Domain.Options.AuthenticationOptions;

[Trait("Category", "Security")]
public sealed class ScimEffectiveGroupsTests
{
    private const string TokenSid = "S-1-5-21-100-200-300-1101";
    private const string ScimSidChild = "S-1-5-21-100-200-300-2001";
    private const string ScimSidParent = "S-1-5-21-100-200-300-2002";
    private const string ScimSidGrand = "S-1-5-21-100-200-300-2003";
    private const string OidA = "11111111-2222-3333-4444-555555555555";
    private static readonly TenantId Tenant = new("tenant-1");

    private static IdentityRealm ScimRealm(
        RealmGroupSource source = RealmGroupSource.Token,
        int maxSyncAgeMinutes = 180,
        ScimStaleSyncBehavior staleBehavior = ScimStaleSyncBehavior.Degrade) => new(
        "realm-1",
        IdentityRealmKind.EntraWorkforce,
        TenantBindingSpecification.Fixed(Tenant),
        source,
        RealmProvisioningMode.Scim,
        issuers: ["https://issuer.example.org"])
    {
        ScimSettings = new RealmScimSettings(
            AllowUpnCorrelation: false,
            RequireCorrelation: true,
            MaxSyncAgeMinutes: maxSyncAgeMinutes,
            StaleSyncBehavior: staleBehavior)
    };

    private static GroupSidResolver CreateResolver(
        InMemoryEffectiveStore store,
        DateTimeOffset now,
        IGroupDenyInspector? denyInspector = null)
    {
        var time = new MutableTimeProvider(now);
        var options = Options.Create(new GatewayOptions { Authentication = new AuthenticationOptions() });
        var correlator = new ScimIdentityCorrelator(store, store);
        var syncTracker = new ScimSyncStateTracker(store, time);
        var scimProvider = new ScimStoreRoleProvider(store);
        var roleResolver = new RoleResolver([scimProvider], new RoleResolverCache(), null, options, store);
        return new GroupSidResolver(
            roleResolver,
            [scimProvider],
            options,
            timeProvider: time,
            scimCorrelator: correlator,
            scimGroupStore: store,
            scimProvisioningStore: store,
            denyInspector: denyInspector,
            syncStateTracker: syncTracker);
    }

    private static ClaimsPrincipal Token(params Claim[] claims) =>
        new(new ClaimsIdentity([new Claim("oid", OidA), .. claims], "Bearer"));

    [Fact]
    public async Task Effective_groups_are_union_of_token_and_scim()
    {
        var now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
        var store = new InMemoryEffectiveStore();
        store.SeedUser("u-1", entraOid: OidA);
        store.SeedGroup("g-scim", ScimSidChild, "SCIM Role");
        store.DirectMemberships.Add(("g-scim", "u-1"));
        store.SyncState = new ScimRealmSyncState(Tenant, "realm-1", now.AddMinutes(-5), now.AddMinutes(-5), 0);

        var sut = CreateResolver(store, now);
        var result = await sut.ResolveAsync(
            Token(new Claim("groups", TokenSid)),
            ScimRealm(),
            Tenant,
            "Bearer",
            CancellationToken.None);

        result.Status.ShouldBe(GroupResolutionStatus.Complete);
        result.GroupSids.Select(s => s.Value).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            .ShouldBe([TokenSid, ScimSidChild]);
    }

    [Fact]
    public async Task Nested_groups_are_flattened()
    {
        var now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
        var store = new InMemoryEffectiveStore();
        store.SeedUser("u-1", entraOid: OidA);
        store.SeedGroup("g-child", ScimSidChild, "Child");
        store.SeedGroup("g-parent", ScimSidParent, "Parent");
        store.SeedGroup("g-grand", ScimSidGrand, "Grand");
        store.DirectMemberships.Add(("g-child", "u-1"));
        store.NestingEdges.Add(("g-parent", "g-child"));
        store.NestingEdges.Add(("g-grand", "g-parent"));
        store.SyncState = new ScimRealmSyncState(Tenant, "realm-1", now.AddMinutes(-1), now.AddMinutes(-1), 0);

        var sut = CreateResolver(store, now);
        var result = await sut.ResolveAsync(Token(), ScimRealm(RealmGroupSource.Resolver), Tenant, "Bearer", CancellationToken.None);

        result.Status.ShouldBe(GroupResolutionStatus.Complete);
        result.GroupSids.Select(s => s.Value).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            .ShouldBe([ScimSidChild, ScimSidParent, ScimSidGrand]);
    }

    [Fact]
    public async Task Nesting_cycle_terminates()
    {
        var now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
        var store = new InMemoryEffectiveStore();
        store.SeedUser("u-1", entraOid: OidA);
        store.SeedGroup("g-1", ScimSidChild, "G1");
        store.SeedGroup("g-2", ScimSidParent, "G2");
        store.DirectMemberships.Add(("g-1", "u-1"));
        store.NestingEdges.Add(("g-2", "g-1"));
        store.NestingEdges.Add(("g-1", "g-2"));
        store.SyncState = new ScimRealmSyncState(Tenant, "realm-1", now.AddMinutes(-1), now.AddMinutes(-1), 0);

        var sut = CreateResolver(store, now);
        var result = await sut.ResolveAsync(Token(), ScimRealm(RealmGroupSource.Resolver), Tenant, "Bearer", CancellationToken.None);

        result.Status.ShouldBe(GroupResolutionStatus.Complete);
        result.GroupSids.Select(s => s.Value).OrderBy(x => x, StringComparer.Ordinal).ToArray()
            .ShouldBe([ScimSidChild, ScimSidParent]);
    }

    [Fact]
    public async Task Nesting_depth_over_limit_degrades()
    {
        var now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
        var store = new InMemoryEffectiveStore();
        store.SeedUser("u-1", entraOid: OidA);
        for (int i = 0; i <= 33; i++)
        {
            store.SeedGroup($"g-{i}", $"S-1-5-21-100-200-300-{3000 + i}", $"G{i}");
            if (i > 0)
            {
                store.NestingEdges.Add(($"g-{i}", $"g-{i - 1}"));
            }
        }
        store.DirectMemberships.Add(("g-0", "u-1"));
        store.SyncState = new ScimRealmSyncState(Tenant, "realm-1", now.AddMinutes(-1), now.AddMinutes(-1), 0);

        var sut = CreateResolver(store, now);
        var result = await sut.ResolveAsync(Token(), ScimRealm(RealmGroupSource.Resolver), Tenant, "Bearer", CancellationToken.None);

        result.Status.ShouldBe(GroupResolutionStatus.Degraded);
        result.GroupSids.ShouldBeEmpty();
    }

    [Fact]
    public async Task Group_without_canonical_sid_is_rejected()
    {
        var now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
        var store = new InMemoryEffectiveStore();
        store.SeedUser("u-1", entraOid: OidA);
        store.SeedGroup("g-bad", null, "finance-team");
        store.DirectMemberships.Add(("g-bad", "u-1"));
        store.SyncState = new ScimRealmSyncState(Tenant, "realm-1", now.AddMinutes(-1), now.AddMinutes(-1), 0);

        var provider = new ScimStoreRoleProvider(store);
        var providerResult = await provider.GetRolesAsync(
            new ResolvedIdentity("u-1", null, null, null, Tenant, "realm-1", []),
            ScimRealm());

        providerResult.Failed.ShouldBeTrue();

        var sut = CreateResolver(store, now);
        var result = await sut.ResolveAsync(Token(), ScimRealm(RealmGroupSource.Resolver), Tenant, "Bearer", CancellationToken.None);

        result.Status.ShouldBe(GroupResolutionStatus.Degraded);
        result.GroupSids.ShouldBeEmpty();
    }

    [Fact]
    public async Task Display_name_is_never_matched()
    {
        var now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
        var store = new InMemoryEffectiveStore();
        store.SeedUser("u-1", entraOid: OidA);
        store.SeedGroup("g-1", ScimSidChild, TokenSid);
        store.DirectMemberships.Add(("g-1", "u-1"));
        store.SyncState = new ScimRealmSyncState(Tenant, "realm-1", now.AddMinutes(-1), now.AddMinutes(-1), 0);

        var effective = await store.GetEffectiveGroupSidsForUserAsync(Tenant, "realm-1", "u-1");
        effective.GroupObjectSids.ShouldBe([ScimSidChild]);
        effective.GroupObjectSids.ShouldNotContain(TokenSid);
    }

    [Fact]
    public async Task Stale_sync_degrades_by_default()
    {
        var now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
        var store = new InMemoryEffectiveStore();
        store.SeedUser("u-1", entraOid: OidA);
        store.SeedGroup("g-1", ScimSidChild, "Role");
        store.DirectMemberships.Add(("g-1", "u-1"));
        store.SyncState = new ScimRealmSyncState(Tenant, "realm-1", now.AddMinutes(-181), now.AddMinutes(-181), 0);

        var sut = CreateResolver(store, now);
        var result = await sut.ResolveAsync(
            Token(new Claim("groups", TokenSid)),
            ScimRealm(RealmGroupSource.Token, maxSyncAgeMinutes: 180, staleBehavior: ScimStaleSyncBehavior.Degrade),
            Tenant,
            "Bearer",
            CancellationToken.None);

        result.Status.ShouldBe(GroupResolutionStatus.Degraded);
        result.GroupSids.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stale_sync_drop_mode_uses_token_groups_only()
    {
        var now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
        var store = new InMemoryEffectiveStore();
        store.SeedUser("u-1", entraOid: OidA);
        store.SeedGroup("g-1", ScimSidChild, "Role");
        store.DirectMemberships.Add(("g-1", "u-1"));
        store.SyncState = new ScimRealmSyncState(Tenant, "realm-1", now.AddMinutes(-200), now.AddMinutes(-200), 0);

        var denyInspector = new GovernanceGroupDenyInspector(
            new InMemoryConsentRepo([]),
            new InMemoryVirtualFilterRepo([]),
            timeProvider: new MutableTimeProvider(now));

        var sut = CreateResolver(store, now, denyInspector);
        var result = await sut.ResolveAsync(
            Token(new Claim("groups", TokenSid)),
            ScimRealm(RealmGroupSource.Token, maxSyncAgeMinutes: 180, staleBehavior: ScimStaleSyncBehavior.DropScimGroups),
            Tenant,
            "Bearer",
            CancellationToken.None);

        result.Status.ShouldBe(GroupResolutionStatus.Complete);
        result.GroupSids.Select(s => s.Value).ToArray().ShouldBe([TokenSid]);
    }

    [Fact]
    public async Task DropScimGroups_never_drops_group_with_deny()
    {
        var now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
        var store = new InMemoryEffectiveStore();
        store.SeedUser("u-1", entraOid: OidA);
        store.SeedGroup("g-1", ScimSidChild, "Restricted");
        store.DirectMemberships.Add(("g-1", "u-1"));
        store.SyncState = new ScimRealmSyncState(Tenant, "realm-1", now.AddMinutes(-200), now.AddMinutes(-200), 0);

        var denyConsent = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            TableIdentifier = new TableIdentifier("src", "s", "t"),
            GranteeType = GranteeType.Group,
            GranteeSid = new Sid(ScimSidChild),
            Effect = ConsentEffect.Deny,
            ValidFrom = now.AddDays(-1),
            ValidTo = now.AddDays(30)
        };

        var denyInspector = new GovernanceGroupDenyInspector(
            new InMemoryConsentRepo([denyConsent]),
            new InMemoryVirtualFilterRepo([]),
            timeProvider: new MutableTimeProvider(now));

        var sut = CreateResolver(store, now, denyInspector);
        var result = await sut.ResolveAsync(
            Token(new Claim("groups", TokenSid)),
            ScimRealm(RealmGroupSource.Token, maxSyncAgeMinutes: 180, staleBehavior: ScimStaleSyncBehavior.DropScimGroups),
            Tenant,
            "Bearer",
            CancellationToken.None);

        result.Status.ShouldBe(GroupResolutionStatus.Degraded);
        result.GroupSids.ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_requests_refresh_last_contact()
    {
        var now = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
        var time = new MutableTimeProvider(now);
        var store = new InMemoryEffectiveStore();
        var tracker = new ScimSyncStateTracker(store, time);

        await tracker.RecordAuthenticatedContactAsync(Tenant, "realm-1", isWrite: false);
        store.UpsertCount.ShouldBe(1);
        store.SyncState!.LastContactAt.ShouldBe(now);

        time.Advance(TimeSpan.FromSeconds(10));
        await tracker.RecordAuthenticatedContactAsync(Tenant, "realm-1", isWrite: false);
        store.UpsertCount.ShouldBe(1);

        time.Advance(TimeSpan.FromSeconds(25));
        await tracker.RecordAuthenticatedContactAsync(Tenant, "realm-1", isWrite: false);
        store.UpsertCount.ShouldBe(2);
        store.SyncState!.LastContactAt.ShouldBe(now.AddSeconds(35));
    }

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class InMemoryConsentRepo(IReadOnlyList<Consent> consents) : IConsentRepository
    {
        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default)
        {
            var items = consents.Where(c => c.TenantId == query.Tenant && (!query.GranteeType.HasValue || c.GranteeType == query.GranteeType.Value)).ToList();
            return Task.FromResult(new ConsentPage(items, null));
        }

        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, Autheris.Domain.Security.ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, Autheris.Domain.Security.ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InMemoryVirtualFilterRepo(IReadOnlyList<VirtualFilterAccessProfile> profiles) : IVirtualFilterRepository
    {
        public Task<VirtualFilterSnapshot> LoadSnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult(new VirtualFilterSnapshot(1, [], profiles));

        public Task<long> GetGenerationAsync(CancellationToken ct = default) => Task.FromResult(1L);
        public Task ApplyAsync(VirtualFilterChangeSet changes, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InMemoryEffectiveStore : IIdentityStoreRepository
    {
        public Dictionary<string, ScimUser> Users { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ScimGroup> Groups { get; } = new(StringComparer.Ordinal);
        public HashSet<(string GroupId, string UserKey)> DirectMemberships { get; } = [];
        public HashSet<(string ParentGroupId, string ChildGroupId)> NestingEdges { get; } = [];
        public ScimRealmSyncState? SyncState { get; set; }
        public int UpsertCount { get; private set; }

        public void SeedUser(string id, string? entraOid = null) =>
            Users[id] = new ScimUser(id, id, id, id, null, true, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, EntraObjectId: entraOid);

        public void SeedGroup(string id, string? objectSid, string displayName) =>
            Groups[id] = new ScimGroup(id, id, displayName, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ObjectSid: objectSid);

        public Task<EffectiveScimGroupsResult> GetEffectiveGroupSidsForUserAsync(TenantId tenantId, string realmId, string userKey, int maxDepth = 32, CancellationToken ct = default)
        {
            var visited = new Dictionary<string, int>(StringComparer.Ordinal);
            var queue = new Queue<(string GroupId, int Depth)>();
            foreach (var (gid, uk) in DirectMemberships)
            {
                if (uk == userKey && visited.TryAdd(gid, 0))
                {
                    queue.Enqueue((gid, 0));
                }
            }

            while (queue.Count > 0)
            {
                var (gid, depth) = queue.Dequeue();
                if (depth > maxDepth)
                {
                    return Task.FromResult(new EffectiveScimGroupsResult(new HashSet<string>(), true));
                }

                foreach (var (parent, child) in NestingEdges)
                {
                    if (child == gid)
                    {
                        var nextDepth = depth + 1;
                        if (nextDepth > maxDepth)
                        {
                            return Task.FromResult(new EffectiveScimGroupsResult(new HashSet<string>(), true));
                        }

                        if (!visited.ContainsKey(parent))
                        {
                            visited[parent] = nextDepth;
                            queue.Enqueue((parent, nextDepth));
                        }
                    }
                }
            }

            var sids = new HashSet<string>(StringComparer.Ordinal);
            bool nonCanonical = false;
            foreach (var gid in visited.Keys)
            {
                var sid = Groups.TryGetValue(gid, out var grp) ? grp.ObjectSid : null;
                if (string.IsNullOrWhiteSpace(sid) || !SidFormat.IsCanonical(sid))
                {
                    nonCanonical = true;
                }
                else
                {
                    sids.Add(sid);
                }
            }

            return Task.FromResult(new EffectiveScimGroupsResult(sids, false, nonCanonical));
        }

        public Task<ScimRealmSyncState?> GetRealmSyncStateAsync(TenantId tenantId, string realmId, CancellationToken ct = default) =>
            Task.FromResult(SyncState);

        public Task UpsertRealmSyncStateAsync(TenantId tenantId, string realmId, DateTimeOffset lastContactAt, DateTimeOffset? lastWriteAt, CancellationToken ct = default)
        {
            UpsertCount++;
            var epoch = SyncState?.DeprovisionEpoch ?? 0;
            SyncState = new ScimRealmSyncState(tenantId, realmId, lastContactAt, lastWriteAt ?? SyncState?.LastWriteAt, epoch);
            return Task.CompletedTask;
        }

        public Task<ScimUser?> GetUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.FromResult(Users.GetValueOrDefault(userKey));
        public Task<ScimUser?> GetUserByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) => Task.FromResult(Users.Values.FirstOrDefault(u => u.ExternalId == externalId));
        public Task<ScimUser?> GetUserByUserNameAsync(TenantId tenantId, string realmId, string userName, CancellationToken ct = default) => Task.FromResult(Users.Values.FirstOrDefault(u => u.UserName == userName));
        public Task<ScimUser?> GetUserByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) => Task.FromResult(Users.Values.FirstOrDefault(u => u.ObjectSid == objectSid));
        public Task<ScimUser?> GetUserByImmutableIdAsync(TenantId tenantId, string realmId, string onPremisesImmutableId, CancellationToken ct = default) => Task.FromResult(Users.Values.FirstOrDefault(u => u.OnPremisesImmutableId == onPremisesImmutableId));
        public Task<ScimUser?> GetUserByEntraObjectIdAsync(TenantId tenantId, string realmId, string entraObjectId, CancellationToken ct = default) => Task.FromResult(Users.Values.FirstOrDefault(u => u.EntraObjectId == entraObjectId));
        public Task<ScimUser?> GetUserByUpnAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default) => Task.FromResult(Users.Values.FirstOrDefault(u => u.UserPrincipalName == userPrincipalName));
        public Task<IReadOnlyList<ScimUser>> GetUsersByEntraObjectIdOrExternalIdAsync(TenantId tenantId, string realmId, string identifier, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimUser>>(Users.Values.Where(u => u.EntraObjectId == identifier || u.ExternalId == identifier).ToList());
        public Task<IReadOnlyList<ScimUser>> GetUsersByUpnOrUserNameAsync(TenantId tenantId, string realmId, string userPrincipalName, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimUser>>(Users.Values.Where(u => string.Equals(u.UserPrincipalName, userPrincipalName, StringComparison.OrdinalIgnoreCase) || string.Equals(u.UserName, userPrincipalName, StringComparison.OrdinalIgnoreCase)).ToList());
        public Task<bool> IsSubjectLockedAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.FromResult(false);
        public Task UpsertUserAsync(ScimUser user, TenantId tenantId, string realmId, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateUserAsync(TenantId tenantId, string realmId, ScimUser user, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ScimUser>> ListUsersAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimUser>>([]);
        public Task<ScimGroup?> GetGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => Task.FromResult(Groups.GetValueOrDefault(groupId));
        public Task<ScimGroup?> GetGroupByExternalIdAsync(TenantId tenantId, string realmId, string externalId, CancellationToken ct = default) => Task.FromResult<ScimGroup?>(null);
        public Task<ScimGroup?> GetGroupByObjectSidAsync(TenantId tenantId, string realmId, string objectSid, CancellationToken ct = default) => Task.FromResult<ScimGroup?>(null);
        public Task UpsertGroupAsync(ScimGroup group, TenantId tenantId, string realmId, CancellationToken ct = default) => Task.CompletedTask;
        public Task CreateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpdateGroupAsync(TenantId tenantId, string realmId, ScimGroup group, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> DeleteGroupAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<ScimGroup>> ListGroupsAsync(TenantId tenantId, string realmId, int startIndex = 1, int count = 100, string? filter = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimGroup>>([]);
        public Task AddGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveGroupMemberAsync(TenantId tenantId, string realmId, string groupId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetGroupMembershipsForUserAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> GetGroupMembersAsync(TenantId tenantId, string realmId, string groupId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task AddGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveGroupNestingEdgeAsync(TenantId tenantId, string realmId, string parentGroupId, string childGroupId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetChildGroupIdsAsync(TenantId tenantId, string realmId, string parentGroupId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<ScimGroupNestingEdge>> ListGroupNestingEdgesAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimGroupNestingEdge>>([]);
        public Task AddLockAsync(ScimLock lockEntry, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ScimLock>> GetLocksAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScimLock>>([]);
        public Task<ScimLockWindow> CountLocksSinceAsync(TenantId tenantId, string realmId, IReadOnlyList<string> sources, DateTimeOffset sinceUtc, int cap, CancellationToken ct = default) => Task.FromResult(new ScimLockWindow(0, null));
        public Task RemoveLockAsync(TenantId tenantId, string realmId, string userKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> IncrementRealmDeprovisionEpochAsync(TenantId tenantId, string realmId, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
