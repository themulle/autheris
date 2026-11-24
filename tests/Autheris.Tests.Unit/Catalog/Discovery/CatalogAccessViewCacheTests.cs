namespace Autheris.Tests.Unit.Catalog.Discovery;

using System.Collections.Frozen;
using System.Security.Claims;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Plugins;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Governance.Classification;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #1044: the caller's access view of the <c>accessStatus</c> filter is kept across requests. A later request must see every
/// revoke, grant and deny at once (local invalidation before the next request), must never use another caller's, group set's or tenant's
/// view, and must not use the cache while the invalidation signal is degraded. Each "request" is a new (scoped) discovery service
/// sharing one cache.
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogAccessViewCacheTests
{
    private const string Caller = "S-1-5-21-1044-1044-1044-1001";
    private const string Other = "S-1-5-21-1044-1044-1044-1002";
    private const string GroupA = "S-1-5-21-1044-1044-1044-3001";
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SecondRequest_UsesTheCachedView_WithoutDecidingTheGrantedTablesAgain()
    {
        var f = new Fixture(count: 40, granted: [3, 7, 21]);

        (await f.FacetsAsync(Caller)).TotalCount.ShouldBe(3);
        f.DiscoverDecisions.ShouldBe(3);

        (await f.FacetsAsync(Caller)).TotalCount.ShouldBe(3);
        f.DiscoverDecisions.ShouldBe(3, "the second request must not decide the candidates again");
    }

    [Fact]
    public async Task RevokeBetweenTwoRequests_RemovesTheTableAtOnce_AndRedecidesOnlyIt()
    {
        var f = new Fixture(count: 40, granted: [3, 7, 21]);
        await f.FacetsAsync(Caller);

        await f.RevokeAsync(7);

        var names = await f.GrantedNamesAsync(Caller);
        names.ShouldBe(["t003", "t021"]);
        f.LastFilterDiscoverDecisions.ShouldBe(0, "a revoked table is no longer a grant candidate");
    }

    [Fact]
    public async Task NewGrantBetweenTwoRequests_AddsTheTableAtOnce()
    {
        var f = new Fixture(count: 40, granted: [3]);
        await f.FacetsAsync(Caller);

        await f.GrantAsync(12);

        (await f.GrantedNamesAsync(Caller)).ShouldBe(["t003", "t012"]);
    }

    [Fact]
    public async Task NewDenyBetweenTwoRequests_RemovesTheTableAtOnce()
    {
        var f = new Fixture(count: 40, granted: [3, 7]);
        await f.FacetsAsync(Caller);

        await f.DenyAsync(3);

        // Facets have no page guard: only the invalidation can remove the table.
        (await f.FacetsAsync(Caller)).TotalCount.ShouldBe(1);
        f.LastFilterDiscoverDecisions.ShouldBe(1, "only the invalidated table is decided again");
        (await f.GrantedNamesAsync(Caller)).ShouldBe(["t007"]);
    }

    [Fact]
    public async Task TenantWideInvalidation_RebuildsTheView()
    {
        // A pattern consent, classification definition or access profile change appends an invalidation of every table.
        var f = new Fixture(count: 40, granted: [3, 7]);
        await f.FacetsAsync(Caller);
        f.Pdp.Deny.Add(7); // only visible to a full rebuild
        f.Epochs.InvalidateAllTables();

        (await f.FacetsAsync(Caller)).TotalCount.ShouldBe(1);
        f.LastFilterDiscoverDecisions.ShouldBe(2, "every candidate is decided again");
    }

    [Fact]
    public async Task OverflowOfTheInvalidationLog_RebuildsTheView()
    {
        var f = new Fixture(count: 10, granted: [3, 7], logCapacity: 4);
        await f.FacetsAsync(Caller);
        f.Pdp.Deny.Add(7);
        for (var i = 0; i < 5; i++)
        {
            await f.Epochs.InvalidateEpochAsync(new TableIdentifier("other", "dbo", $"x{i}"));
        }

        (await f.FacetsAsync(Caller)).TotalCount.ShouldBe(1);
        f.LastFilterDiscoverDecisions.ShouldBe(2);
    }

    [Fact]
    public async Task CatalogVersionChange_RebuildsTheView()
    {
        var f = new Fixture(count: 10, granted: [3, 7]);
        await f.FacetsAsync(Caller);
        f.Pdp.Deny.Add(3);
        f.Catalog.Version = new CatalogVersion(1, 2);

        (await f.FacetsAsync(Caller)).TotalCount.ShouldBe(1);
        f.LastFilterDiscoverDecisions.ShouldBe(2);
    }

    [Fact]
    public async Task GovernanceEpochAdvance_RebuildsTheView()
    {
        // Protection gates and tenant access-policy settings decide Discover as well; any governance change of the tenant advances its
        // epoch, which ends every cached view of the tenant.
        var f = new Fixture(count: 10, granted: [3, 7]);
        await f.FacetsAsync(Caller);
        f.Pdp.Deny.Add(7);
        f.GovernanceStore.Epoch++;
        f.GovernanceEpochs.InvalidateTenant("tenant-a");

        (await f.FacetsAsync(Caller)).TotalCount.ShouldBe(1);
        f.LastFilterDiscoverDecisions.ShouldBe(2);
    }

    [Fact]
    public async Task AnotherUser_AnotherGroupSet_AnotherTenant_AndAnotherClaimSet_NeverShareAView()
    {
        var f = new Fixture(count: 20, granted: [3]);
        f.Consents.Active.Add(Fixture.Allow(Other, f.Tables[5].Identifier));
        f.Consents.Active.Add(Fixture.Allow(GroupA, f.Tables[9].Identifier));
        await f.FacetsAsync(Caller);

        // Facets have no page guard: a shared view would show the first caller's count.
        (await f.FacetsAsync(Other)).TotalCount.ShouldBe(1);
        (await f.GrantedNamesAsync(Other)).ShouldBe(["t005"]);
        (await f.GrantedNamesAsync(Caller, groups: [GroupA])).ShouldBe(["t003", "t009"]);
        (await f.GrantedNamesAsync(Caller, tenant: "tenant-b")).ShouldBeEmpty();

        // Same user and groups, another scope claim: a separate view (the claim set is part of the key).
        var before = f.DiscoverDecisions;
        await f.FacetsAsync(Caller, scopes: "Autheris.Catalog.Read Other.Scope");
        f.DiscoverDecisions.ShouldBeGreaterThan(before);
    }

    [Fact]
    public async Task DegradedInvalidationSignal_BypassesTheCache()
    {
        var f = new Fixture(count: 10, granted: [3, 7]);
        f.Epochs.Degraded = true;
        await f.FacetsAsync(Caller);
        await f.FacetsAsync(Caller);

        f.DiscoverDecisions.ShouldBe(4, "no view is stored or read without a trustworthy invalidation signal");
        f.Cache.CurrentEstimatedSize.ShouldBe(0);
    }

    [Fact]
    public async Task AuthorizationHooks_BypassTheCache()
    {
        var f = new Fixture(count: 10, granted: [3], hooks: true);
        await f.FacetsAsync(Caller);
        await f.FacetsAsync(Caller);

        f.DiscoverDecisions.ShouldBe(2);
    }

    [Fact]
    public async Task ConsentExpiry_EndsTheView()
    {
        var f = new Fixture(count: 10, granted: [3, 7]);
        f.Consents.Active[1] = Fixture.Allow(Caller, f.Tables[7].Identifier, validTo: Now.AddMinutes(1));
        await f.FacetsAsync(Caller);
        f.Time.Advance(TimeSpan.FromMinutes(2));

        (await f.FacetsAsync(Caller)).TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task ScheduledDeny_EndsTheView()
    {
        var f = new Fixture(count: 10, granted: [3, 7]);
        f.Consents.Active.Add(Fixture.Allow(Caller, f.Tables[7].Identifier, ConsentEffect.Deny, validFrom: Now.AddMinutes(1)));
        f.Pdp.DenyFrom[7] = Now.AddMinutes(1);
        await f.FacetsAsync(Caller);
        (await f.FacetsAsync(Caller)).TotalCount.ShouldBe(2);
        f.Time.Advance(TimeSpan.FromMinutes(2));

        (await f.FacetsAsync(Caller)).TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task EntryOlderThanTheTtl_IsNotUsed()
    {
        var f = new Fixture(count: 10, granted: [3]);
        await f.FacetsAsync(Caller);
        f.Time.Advance(TimeSpan.FromSeconds(301));
        await f.FacetsAsync(Caller);

        f.DiscoverDecisions.ShouldBe(2);
    }

    [Fact]
    public async Task PageGuard_DropsAViewThatContradictsTheFreshDecisionOfAListedItem()
    {
        // A lost invalidation: the policy denies t007 now, but no signal reached this replica. The listed item is decided freshly,
        // so the stale view is detected, dropped, and the page is listed again without it.
        var f = new Fixture(count: 10, granted: [3, 7]);
        await f.FacetsAsync(Caller);
        f.Pdp.Deny.Add(7);

        var page = await f.Service().ListAsync(new DiscoveryQuery(First: 50, Access: DiscoveryAccessState.Granted), f.CallerOf(Caller));

        page.Items.Select(static o => o.Name).ShouldBe(["t003"]);
        page.TotalCount.ShouldBe(1);
        (await f.FacetsAsync(Caller)).TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task LoadRacingATenantWideInvalidation_IsNotStored()
    {
        // Compare-and-set: an invalidation the log cannot resolve per table, during the load, leaves nothing behind.
        var f = new Fixture(count: 10, granted: [3]);
        f.Pdp.OnDecide = () => f.Epochs.InvalidateAllTables();
        await f.FacetsAsync(Caller);

        f.Cache.CurrentEstimatedSize.ShouldBe(0);
    }

    [Fact]
    public async Task LoadRacingATableRevoke_IsStored_AndTheNextRequestRedecidesThatTable()
    {
        var f = new Fixture(count: 10, granted: [3, 7]);
        f.Pdp.OnDecide = () =>
        {
            f.Pdp.OnDecide = null;
            f.Consents.Active.RemoveAll(c => c.TableIdentifier == f.Tables[7].Identifier);
            f.Epochs.InvalidateEpochAsync(f.Tables[7].Identifier).GetAwaiter().GetResult();
        };
        (await f.FacetsAsync(Caller)).TotalCount.ShouldBe(2, "decided before the revoke");

        (await f.FacetsAsync(Caller)).TotalCount.ShouldBe(1);
        f.Cache.CurrentEstimatedSize.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task AnnouncementMissedFromAnotherReplica_RebuildsTheView()
    {
        // The cluster counted an invalidation this replica's log never saw (a lost pub/sub message): the view is not trusted.
        var f = new Fixture(count: 10, granted: [3, 7]);
        await f.FacetsAsync(Caller);
        f.Pdp.Deny.Add(7);
        f.Epochs.ClusterSequence++;

        (await f.FacetsAsync(Caller)).TotalCount.ShouldBe(1);
        f.LastFilterDiscoverDecisions.ShouldBe(2);
    }

    [Fact]
    public async Task UnreadableClusterSequence_BypassesTheCache()
    {
        var f = new Fixture(count: 10, granted: [3]);
        f.Epochs.ClusterSequenceUnavailable = true;
        await f.FacetsAsync(Caller);
        await f.FacetsAsync(Caller);

        f.DiscoverDecisions.ShouldBe(2);
    }

    [Fact]
    public async Task SearchWithAccessFilter_DropsAViewThatContradictsAListedItem()
    {
        var f = new Fixture(count: 10, granted: [3, 7]);
        await f.FacetsAsync(Caller);
        f.Pdp.Deny.Add(7);

        var hits = await f.Service().SearchAsync("t0", new DiscoverySearchOptions(Limit: 50, Access: DiscoveryAccessState.Granted), f.CallerOf(Caller));

        hits.Select(static o => o.Name).ShouldBe(["t003"]);
    }

    [Fact]
    public async Task SizeLimit_EvictsEntries_AndAnOversizedEntryIsNotStored()
    {
        var epochs = new FakeEpochs(capacity: 16);
        var catalog = new FakeCatalogVersion();
        var time = new AdjustableClassificationTimeProvider(Now);
        var options = Options.Create(new CatalogDiscoveryOptions
        {
            Discovery = new CatalogDiscoverySettings { AccessViewCache = new CatalogAccessViewCacheSettings { SizeLimitMegabytes = 1, MaxEntryMegabytes = 1 } }
        });
        using var cache = new CatalogAccessViewCache(options, epochs, catalog, null, time);
        var stamp = (await cache.ReadStampAsync(new TenantId("tenant-a"), CancellationToken.None))!;

        // ~200 KB per entry: ten entries cannot all stay within 1 MB.
        var stored = 0;
        for (var i = 0; i < 10; i++)
        {
            stored += await cache.TrySetAsync($"k{i}", new TenantId("tenant-a"), Entry(stamp, 2_000), CancellationToken.None) ? 1 : 0;
        }

        stored.ShouldBeGreaterThan(0);
        cache.CurrentEstimatedSize.ShouldBeLessThanOrEqualTo(1024 * 1024);
        Enumerable.Range(0, 10).Count(i => cache.TryGet($"k{i}", stamp) is not null).ShouldBeLessThan(10);

        // ~1.9 MB: above the per-entry cap.
        (await cache.TrySetAsync("big", new TenantId("tenant-a"), Entry(stamp, 20_000), CancellationToken.None)).ShouldBeFalse();
        cache.TryGet("big", stamp).ShouldBeNull();
    }

    [Fact]
    public void InvalidationLog_ProvesOnlyCompleteRanges()
    {
        var log = new PolicyInvalidationLog(capacity: 3);
        log.InvalidatedSince(0)!.ShouldBeEmpty();
        log.Append("a.b.c");
        log.Append("A.B.D");
        log.InvalidatedSince(0)!.OrderBy(static k => k).ShouldBe(["a.b.c", "a.b.d"]);
        log.InvalidatedSince(1)!.ShouldBe(["a.b.d"]);
        log.InvalidatedSince(3).ShouldBeNull("a generation of the future is not this log's");

        log.Append(PolicyInvalidationLog.AllTables);
        log.InvalidatedSince(1).ShouldBeNull("an invalidation of every table cannot be revalidated per table");

        log.Append("x.y.z");
        log.Append("x.y.w");
        log.InvalidatedSince(3)!.Count.ShouldBe(2);
        log.InvalidatedSince(1).ShouldBeNull("the range fell out of the ring");
    }

    private static AccessViewEntry Entry(AccessViewStamp stamp, int tables) => new(
        ReadGrantCandidates.All,
        Enumerable.Range(0, tables).ToFrozenDictionary(static i => new TableIdentifier("d", "s", $"t{i}"), static _ => true),
        stamp,
        Now.AddMinutes(5));

    private sealed class Fixture
    {
        private int _filterMark;

        public Fixture(int count, int[] granted, int logCapacity = PolicyInvalidationLog.DefaultCapacity, bool hooks = false)
        {
            for (var i = 0; i < count; i++)
            {
                Repo.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", $"t{i:000}"));
            }

            foreach (var i in granted)
            {
                Consents.Active.Add(Allow(Caller, Tables[i].Identifier));
            }

            Epochs = new FakeEpochs(logCapacity);
            Pdp = new PolicyPdp(this);
            var hookHost = new Autheris.Tests.Unit.Policy.Pdp.DenyingHookHost(hasHooks: hooks);
            GovernanceEpochs = new Autheris.Application.Governance.Caching.GovernanceEpochProvider(GovernanceStore, timeProvider: Time);
            Cache = new CatalogAccessViewCache(Options.Create(new CatalogDiscoveryOptions()), Epochs, Catalog, null, Time, Options.Create(Gateway()), hookHost, GovernanceEpochs);
        }

        public FakeDiscoveryTableRepository Repo { get; } = new();
        public List<TableMetadata> Tables => Repo.Tables;
        public FakeDiscoveryConsentRepository Consents { get; } = new();
        public FakeEpochs Epochs { get; }
        public FakeCatalogVersion Catalog { get; } = new();
        public MutableGovernanceEpochStore GovernanceStore { get; } = new();
        public Autheris.Application.Governance.Caching.GovernanceEpochProvider GovernanceEpochs { get; }
        public AdjustableClassificationTimeProvider Time { get; } = new(Now);
        public PolicyPdp Pdp { get; }
        public CatalogAccessViewCache Cache { get; }

        public int DiscoverDecisions => Pdp.FilterDecisions;
        public int LastFilterDiscoverDecisions => Pdp.FilterDecisions - _filterMark;

        public static Consent Allow(string sid, TableIdentifier table, ConsentEffect effect = ConsentEffect.Allow, DateTimeOffset? validFrom = null, DateTimeOffset? validTo = null) => new()
        {
            TableIdentifier = table,
            Effect = effect,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(sid),
            TenantId = new TenantId("tenant-a"),
            ValidFrom = validFrom ?? Now.AddDays(-1),
            ValidTo = validTo ?? Now.AddDays(30)
        };

        public DiscoveryService Service() => new(
            Repo,
            Pdp,
            Consents,
            new FakeDiscoveryApprovalRepository(),
            new FakeDiscoveryOwnershipRepository(),
            new FakeDiscoveryArtifactStore(),
            new FakeAuditWriter(),
            Options.Create(Gateway()),
            Options.Create(new CatalogDiscoveryOptions()),
            timeProvider: Time,
            logger: NullLogger<DiscoveryService>.Instance,
            accessViewCache: Cache);

        // Default plugin options: hooks enabled and allowed; whether hooks exist is the plugin host's answer.
        private static GatewayOptions Gateway() => new() { Tenancy = TenancyShape.Multi.Options() };

        public CatalogCaller CallerOf(string sid, string[]? groups = null, string tenant = "tenant-a", string scopes = "Autheris.Catalog.Read")
        {
            var principal = DiscoveryTestBuilder.User(sid, tenant, scopes: scopes);
            if (groups is { Length: > 0 })
            {
                ((ClaimsIdentity)principal.Identity!).AddClaims(groups.Select(static g => new Claim(ClaimTypes.GroupSid, g, ClaimValueTypes.String, IdentityClaimIssuers.Pipeline)));
            }

            return DiscoveryTestBuilder.Caller(principal, new TenantId(tenant));
        }

        public Task<DiscoveryFacets> FacetsAsync(string sid, string scopes = "Autheris.Catalog.Read")
        {
            _filterMark = Pdp.FilterDecisions;
            return Service().FacetsAsync(DiscoveryAccessState.Granted, CallerOf(sid, scopes: scopes)).AsTask();
        }

        public async Task<List<string>> GrantedNamesAsync(string sid, string[]? groups = null, string tenant = "tenant-a")
        {
            _filterMark = Pdp.FilterDecisions;
            var page = await Service().ListAsync(new DiscoveryQuery(First: 200, Access: DiscoveryAccessState.Granted), CallerOf(sid, groups, tenant));
            return page.Items.Select(static o => o.Name).ToList();
        }

        public Task RevokeAsync(int index)
        {
            Consents.Active.RemoveAll(c => c.TableIdentifier == Tables[index].Identifier && c.GranteeSid == new Sid(Caller));
            return Epochs.InvalidateEpochAsync(Tables[index].Identifier);
        }

        public Task GrantAsync(int index)
        {
            Consents.Active.Add(Allow(Caller, Tables[index].Identifier));
            return Epochs.InvalidateEpochAsync(Tables[index].Identifier);
        }

        public Task DenyAsync(int index)
        {
            Pdp.Deny.Add(index);
            return Epochs.InvalidateEpochAsync(Tables[index].Identifier);
        }
    }

    /// <summary>
    /// Zero trust as the real decision point: allows a table only for a grant of the requesting user or group; <see cref="Deny"/> and
    /// <see cref="DenyFrom"/> deny on top. Counts the Discover decisions of the access filter (allow-only batches).
    /// </summary>
    private sealed class PolicyPdp(Fixture f) : IAccessDecisionPoint
    {
        public HashSet<int> Deny { get; } = [];
        public Dictionary<int, DateTimeOffset> DenyFrom { get; } = [];
        public int FilterDecisions { get; private set; }
        public Action? OnDecide { get; set; }

        public ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default) => ValueTask.FromResult(Decide(request.Subject, request.Object));

        public ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(Autheris.Domain.Security.SecurityPrincipalContext subject, IReadOnlyList<TableMetadata> objects, AccessOperation operation, AccessTransport transport, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<TableAccessDecision>>(objects.Select(o => operation == AccessOperation.Discover ? Decide(subject, o) : TableAccessDecision.Denied(o.Identifier, "no write")).ToList());

        public ValueTask<IReadOnlyList<bool>> DecideAllowedManyAsync(Autheris.Domain.Security.SecurityPrincipalContext subject, IReadOnlyList<TableMetadata> objects, CancellationToken ct = default)
        {
            FilterDecisions += objects.Count;
            var allowed = objects.Select(o => Decide(subject, o).IsAllowed).ToList();
            OnDecide?.Invoke(); // a change right after the decisions were made, before the view is stored
            return ValueTask.FromResult<IReadOnlyList<bool>>(allowed);
        }

        public ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default) => throw new NotSupportedException();

        private TableAccessDecision Decide(Autheris.Domain.Security.SecurityPrincipalContext subject, TableMetadata table)
        {
            var index = f.Tables.IndexOf(table);
            var now = f.Time.GetUtcNow();
            var granted = f.Consents.Active.Any(c =>
                c.Effect == ConsentEffect.Allow && c.TableIdentifier == table.Identifier && c.TenantId == subject.TenantId && c.IsActive(now) &&
                c.GranteeSid is { } g && (g == subject.UserSid || subject.GroupSids.Contains(g)));
            var denied = Deny.Contains(index) || (DenyFrom.TryGetValue(index, out var from) && now >= from);
            return granted && !denied
                ? TableAccessDecision.Allowed(table.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
                : TableAccessDecision.Denied(table.Identifier, "Denied");
        }
    }

    private sealed class FakeEpochs(int capacity) : IEpochValidationService
    {
        private readonly PolicyInvalidationLog _log = new(capacity);

        public bool Degraded { get; set; }
        public long ClusterSequence { get; set; }
        public bool ClusterSequenceUnavailable { get; set; }

        public long? InvalidationGeneration => Degraded ? null : _log.Generation;

        public ValueTask<long?> GetClusterInvalidationSequenceAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<long?>(ClusterSequenceUnavailable ? null : ClusterSequence);

        public IReadOnlySet<string>? GetInvalidatedSince(long generation) => _log.InvalidatedSince(generation);

        public void InvalidateAllTables() => _log.Append(PolicyInvalidationLog.AllTables);

        public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default)
        {
            _log.Append(table.ToString().ToLowerInvariant());
            return Task.CompletedTask;
        }

        public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default) => Task.FromResult(true);
        public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(tables.Distinct().ToDictionary(static t => t, static _ => 1L));
    }

    private sealed class MutableGovernanceEpochStore : Autheris.Application.Governance.Caching.IGovernanceEpochStore
    {
        public long Epoch { get; set; } = 1;
        public Task<long> GetEpochAsync(TenantId tenant, CancellationToken ct = default) => Task.FromResult(Epoch);
        public Task<long> BumpEpochAsync(TenantId tenant, CancellationToken ct = default) => Task.FromResult(++Epoch);
    }

    private sealed class FakeCatalogVersion : ICatalogVersionProvider
    {
        public CatalogVersion Version { get; set; } = new(1, 1);
        public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) => ValueTask.FromResult(Version);
        public Task<string> GetVersionAsync(CancellationToken ct = default) => Task.FromResult($"{Version.StructureVersion}.{Version.ContentVersion}");
    }
}
