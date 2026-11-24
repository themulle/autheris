namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.External;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog.Discovery;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ExternalCatalogSurfaceParityTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string UserSid = "S-1-PARITY-USER";

    private static ClaimsPrincipal DelegatedUser(string tenant = TenantA) =>
        ExternalCatalogTestHarness.BuildDelegatedUserPrincipal(tenant, UserSid, scopes: "Apps.Read");

    private static IReadOnlyList<string?> ObjectIds(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("objectId").GetString()).ToList();

    private static void AddConsent(ExternalCatalogTestHarness harness, string tenant, TableMetadata table, Sid sid) =>
        harness.Consents.Consents.Add(new Consent
        {
            Id = Guid.NewGuid(),
            TableId = table.Table.Id,
            TableIdentifier = table.Identifier,
            TenantId = new TenantId(tenant),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = sid,
            ValidFrom = ExternalCatalogTestHarness.FixedNow.AddDays(-1),
            ValidTo = ExternalCatalogTestHarness.FixedNow.AddDays(1),
            ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UserSurfaces_ShowIdenticalObjectVisibility_AcrossListSearchFacetsAndDetail(bool useSnapshot)
    {
        await using var harness = ExternalCatalogTestHarness.Create(catalogSnapshot: useSnapshot);
        var openGranted = harness.AddTable("sales", "shared_orders", ownerTenantId: TenantA, hidden: false, columns: [("id", false, false)]);
        var hiddenOwned = harness.AddTable("sales", "hidden_owned_orders", ownerTenantId: TenantA, hidden: true, columns: [("id", false, false)]);
        var hiddenUnowned = harness.AddTable("sales", "hidden_secret_orders", ownerTenantId: TenantA, hidden: true, columns: [("id", false, false)]);
        var foreignConsented = harness.AddTable("hr", "foreign_orders", ownerTenantId: TenantB, hidden: false, columns: [("id", false, false)]);
        var foreignUnconsented = harness.AddTable("hr", "foreign_private", ownerTenantId: TenantB, hidden: false, columns: [("id", false, false)]);

        var user = DelegatedUser(TenantA);
        var sid = new Sid(UserSid);

        harness.Owners.AssignOwner(hiddenOwned.Identifier, UserSid, "Owner");
        AddConsent(harness, TenantA, openGranted, sid);
        AddConsent(harness, TenantA, hiddenOwned, sid);
        AddConsent(harness, TenantA, hiddenUnowned, sid);
        AddConsent(harness, TenantA, foreignConsented, sid);

        string[] expectedVisible =
        [
            foreignConsented.Identifier.ToString(),
            hiddenOwned.Identifier.ToString(),
            openGranted.Identifier.ToString()
        ];

        var listPage = (await harness.ListObjectsAsync(user)).ReadJson();
        ObjectIds(listPage).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ShouldBe(expectedVisible);

        var searchPage = (await harness.ListObjectsAsync(user, q: "orders")).ReadJson();
        ObjectIds(searchPage).OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ShouldBe(expectedVisible);

        var facets = (await harness.GetFacetsAsync(user)).ReadJson();
        facets.GetProperty("totalCount").GetInt32().ShouldBe(expectedVisible.Length);

        var searchFacets = (await harness.GetFacetsAsync(user, q: "orders")).ReadJson();
        searchFacets.GetProperty("totalCount").GetInt32().ShouldBe(expectedVisible.Length);

        foreach (var visibleId in expectedVisible)
        {
            var ok = await harness.GetObjectByIdAsync(user, visibleId);
            ok.StatusCode.ShouldBe(200);
        }

        (await harness.GetObjectByIdAsync(user, hiddenUnowned.Identifier.ToString())).StatusCode.ShouldBe(404);
        (await harness.GetObjectByIdAsync(user, foreignUnconsented.Identifier.ToString())).StatusCode.ShouldBe(404);
        (await harness.GetObjectByIdAsync(user, hiddenUnowned.Table.Id.ToString())).StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task ServicePrincipalRules_RemainExplicit_ForListFacetsAndObjectDetail()
    {
        await using var harness = ExternalCatalogTestHarness.Create(catalogSnapshot: true);
        var visibleTbl = harness.AddTable("sales", "app_orders", ownerTenantId: TenantA, hidden: false, columns: [("id", false, false)]);
        var hiddenTbl = harness.AddTable("sales", "app_hidden", ownerTenantId: TenantA, hidden: true, columns: [("id", false, false)]);
        var foreignTbl = harness.AddTable("hr", "foreign_app", ownerTenantId: TenantB, hidden: false, columns: [("id", false, false)]);

        harness.WorkflowRepo.Systems.Add(new ExternalWorkflowSystem(
            Guid.NewGuid(),
            new TenantId(TenantA),
            "wf-admin",
            "wf-admin",
            "https://example.invalid/hook",
            "secret-ref",
            true,
            "admin",
            ExternalCatalogTestHarness.FixedNow,
            null));

        var adminApp = ExternalCatalogTestHarness.BuildAppPrincipal(
            TenantA,
            "wf-admin",
            "Autheris.Catalog.Read",
            "Autheris.Catalog.Read.Admin");
        var basicApp = ExternalCatalogTestHarness.BuildAppPrincipal(
            TenantA,
            "wf-admin",
            "Autheris.Catalog.Read");

        // Admin workflow app sees own visible + own hidden across list, facets, and detail, never foreign unconsented tables.
        var adminList = ObjectIds((await harness.ListObjectsAsync(adminApp)).ReadJson());
        adminList.ShouldBe([hiddenTbl.Identifier.ToString(), visibleTbl.Identifier.ToString()], ignoreOrder: true);
        (await harness.GetFacetsAsync(adminApp)).ReadJson().GetProperty("totalCount").GetInt32().ShouldBe(2);
        (await harness.GetObjectByIdAsync(adminApp, hiddenTbl.Identifier.ToString())).StatusCode.ShouldBe(200);
        (await harness.GetObjectByIdAsync(adminApp, foreignTbl.Identifier.ToString())).StatusCode.ShouldBe(404);

        // Non-admin app sees only non-hidden own-tenant table across list, facets, and detail.
        var basicList = ObjectIds((await harness.ListObjectsAsync(basicApp)).ReadJson());
        basicList.ShouldBe([visibleTbl.Identifier.ToString()]);
        (await harness.GetFacetsAsync(basicApp)).ReadJson().GetProperty("totalCount").GetInt32().ShouldBe(1);
        (await harness.GetObjectByIdAsync(basicApp, hiddenTbl.Identifier.ToString())).StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task TenantConsentTablesProvider_CachesHit_InvalidatesOnEpochAndNotifier_AndFailsClosedWhenDegraded()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        var epochs = new FakeEpochService { InvalidationGeneration = 1, ClusterSequence = 10 };
        var notifier = new StubGovernanceNotifier();
        var repo = Substitute.For<IConsentRepository>();
        var expected = new TableIdentifier("sales", "dbo", "consented");
        repo.GetTablesWithConsentsForTenantAsync(new TenantId(TenantA), Arg.Any<CancellationToken>())
            .Returns([expected]);

        using var provider = new TenantConsentTablesProvider(epochs, changeNotifier: notifier, timeProvider: clock);
        var tenant = new TenantId(TenantA);

        (await provider.GetTablesWithConsentsForTenantAsync(repo, tenant)).ShouldContain(expected);
        (await provider.GetTablesWithConsentsForTenantAsync(repo, tenant)).ShouldContain(expected);
        await repo.Received(1).GetTablesWithConsentsForTenantAsync(tenant, Arg.Any<CancellationToken>());

        epochs.InvalidationGeneration = 2;
        (await provider.GetTablesWithConsentsForTenantAsync(repo, tenant)).ShouldContain(expected);
        await repo.Received(2).GetTablesWithConsentsForTenantAsync(tenant, Arg.Any<CancellationToken>());

        notifier.Emit(new GovernanceChangeEvent(TenantA, 1, null, clock.GetUtcNow()));
        (await provider.GetTablesWithConsentsForTenantAsync(repo, tenant)).ShouldContain(expected);
        await repo.Received(3).GetTablesWithConsentsForTenantAsync(tenant, Arg.Any<CancellationToken>());

        clock.Advance(TimeSpan.FromSeconds(61));
        (await provider.GetTablesWithConsentsForTenantAsync(repo, tenant)).ShouldContain(expected);
        await repo.Received(4).GetTablesWithConsentsForTenantAsync(tenant, Arg.Any<CancellationToken>());

        epochs.ClusterSequence = null;
        _ = await provider.GetTablesWithConsentsForTenantAsync(repo, tenant);
        _ = await provider.GetTablesWithConsentsForTenantAsync(repo, tenant);
        await repo.Received(6).GetTablesWithConsentsForTenantAsync(tenant, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CatalogVisibilityEvaluator_ShortCircuitsConsentLookup_WhenCandidatesAreInOwnerOrSharedScope()
    {
        var consentRepo = Substitute.For<IConsentRepository>();
        var options = Options.Create(new GatewayOptions
        {
            Tenancy = new TenancyOptions { DefaultTenantId = "tenant-owner" },
            Catalog = new DataCatalogOptions { SharedTables = ["ref.dbo.shared_tbl"] }
        });
        var evaluator = new CatalogVisibilityEvaluator(options);

        var ownTable = DiscoveryTestBuilder.MakeTable("sales", "own_tbl", ownerTenantId: TenantA);
        var sharedTable = DiscoveryTestBuilder.MakeTable("ref", "shared_tbl", ownerTenantId: null);
        var ownerOwnedTable = DiscoveryTestBuilder.MakeTable("core", "owner_tbl", ownerTenantId: "tenant-owner");
        var unassignedTable = DiscoveryTestBuilder.MakeTable("core", "unassigned_tbl", ownerTenantId: null);

        var filteredA = await evaluator.FilterInScopeAsync([ownTable, sharedTable], new TenantId(TenantA), consentRepo);
        filteredA.Count.ShouldBe(2);
        await consentRepo.DidNotReceive().GetTablesWithConsentsForTenantAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>());

        var filteredOwner = await evaluator.FilterInScopeAsync([ownerOwnedTable, unassignedTable], new TenantId("tenant-owner"), consentRepo);
        filteredOwner.Count.ShouldBe(2);
        await consentRepo.DidNotReceive().GetTablesWithConsentsForTenantAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void KeysetComparator_MatchesStringOrder_AcrossPunctuationAndCaseTieBreaks_ZeroAllocations()
    {
        TableIdentifier[] ids =
        [
            new("a-b", "c", "d"),
            new("a", "b", "c"),
            new("Sales", "Dbo", "Orders"),
            new("sales", "dbo", "orders"),
            new("sales", "dbo", "orders_v2"),
            new("a", "b", "z")
        ];

        foreach (var left in ids)
        {
            foreach (var right in ids)
            {
                var expected = Math.Sign(DiscoveryService.CompareTableKeys(left.ToString(), right.ToString()));
                var actualIdCmp = Math.Sign(DiscoveryService.CompareTableKeys(left, right));
                var actualSpanCmp = Math.Sign(DiscoveryService.CompareTableKey(left, right.ToString().AsSpan()));

                actualIdCmp.ShouldBe(expected, $"Pair ({left}, {right}) failed span comparison parity.");
                actualSpanCmp.ShouldBe(expected, $"Pair ({left}, {right}) failed AfterKey span parity.");
                DiscoveryService.EqualsTableKey(left, right.ToString().AsSpan()).ShouldBe(
                    string.Equals(left.ToString(), right.ToString(), StringComparison.OrdinalIgnoreCase));
            }
        }

        var a = ids[0];
        var b = ids[1];
        ReadOnlySpan<char> keySpan = "a.b.c";
        _ = DiscoveryService.CompareTableKeys(a, b);
        _ = DiscoveryService.CompareTableKey(a, keySpan);
        _ = DiscoveryService.EqualsTableKey(b, keySpan);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var sum = 0;
        for (var i = 0; i < 1000; i++)
        {
            sum += DiscoveryService.CompareTableKeys(a, b);
            sum += DiscoveryService.CompareTableKey(a, keySpan);
            if (DiscoveryService.EqualsTableKey(b, keySpan))
            {
                sum++;
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        sum.ShouldNotBe(0);
        allocated.ShouldBe(0);
    }

    [Fact]
    public async Task ExternalCatalog_WithSnapshot_ReadsZeroFullCatalogQueries_AndBatchesPageArtifacts()
    {
        var t1 = DiscoveryTestBuilder.MakeTable("sales", "t1", ownerTenantId: TenantA);
        var t2 = DiscoveryTestBuilder.MakeTable("sales", "t2", ownerTenantId: TenantA);
        var t3 = DiscoveryTestBuilder.MakeTable("sales", "t3", ownerTenantId: TenantA);
        var all = new[] { t1, t2, t3 };

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.QueryTablesAsync(Arg.Any<CatalogTableQuery>(), Arg.Any<CancellationToken>())
            .Returns<CatalogTablePage>(_ => throw new InvalidOperationException("Whole-catalog QueryTablesAsync must not be called when snapshot exists."));
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<TableMetadata>>(_ => throw new InvalidOperationException("GetAllTablesAsync must not be called when snapshot exists."));

        var snapshotProvider = Substitute.For<ICatalogSnapshotProvider>();
        var snapshot = CatalogSnapshot.Create(new CatalogVersion(1, 1), all, []);
        snapshotProvider.GetSnapshotAsync(Arg.Any<CancellationToken>()).Returns(snapshot);

        var artifacts = Substitute.For<IDiscoveryArtifactStore>();
        var uploadArtifact = new DiscoveryArtifacts(t1.Table.Id, TenantA, null, null, "upload-sha", "admin", DateTimeOffset.UtcNow, Provenance: ArtifactProvenance.Upload);
        var importArtifact = new DiscoveryArtifacts(t1.Table.Id, TenantA, null, null, "import-sha", "admin", DateTimeOffset.UtcNow, Provenance: ArtifactProvenance.Import);
        artifacts.GetAllForTablesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<DiscoveryArtifacts>>
            {
                [t1.Table.Id] = [importArtifact, uploadArtifact]
            });

        var wfRepo = Substitute.For<Autheris.Application.ConsentRequests.IConsentWorkflowConfigRepository>();
        var changeStore = new InMemoryCatalogChangeStore();
        var options = Options.Create(new GatewayOptions { Tenancy = new TenancyOptions { DefaultTenantId = null } });
        var reader = new ExternalCatalogReader(
            repo,
            Substitute.For<IDiscoveryService>(),
            new FakeDiscoveryConsentRepository(),
            new FakeDiscoveryOwnershipRepository(),
            wfRepo,
            changeStore,
            options,
            artifacts,
            snapshotProvider: snapshotProvider);

        var app = ExternalCatalogTestHarness.BuildAppPrincipal(TenantA, "wf-client");
        var list = await reader.ListObjectsAsync(app, new TenantId(TenantA), new ExternalCatalogListQuery());
        list.IsSuccess.ShouldBeTrue();
        list.Value!.Items.Count.ShouldBe(3);

        await artifacts.Received(1).GetAllForTablesAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 3),
            Arg.Any<CancellationToken>());
        await artifacts.DidNotReceive().GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        var facets = await reader.GetFacetsAsync(app, new TenantId(TenantA));
        facets.IsSuccess.ShouldBeTrue();
        facets.Value!.TotalCount.ShouldBe(3);

        var obj = await reader.GetObjectAsync(app, new TenantId(TenantA), t1.Identifier.ToString());
        obj.IsSuccess.ShouldBeTrue();

        var changes = await reader.GetChangesAsync(app, new TenantId(TenantA), sinceSeq: 0, limit: 10);
        changes.IsSuccess.ShouldBeTrue();

        await artifacts.Received(1).GetAsync(t1.Table.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CatalogChangeIndexer_BatchesArtifactsAndProtections_AndSelectsUploadArtifact()
    {
        var t1 = DiscoveryTestBuilder.MakeTable("sales", "idx_1", ownerTenantId: TenantA);
        var t2 = DiscoveryTestBuilder.MakeTable("sales", "idx_2", ownerTenantId: TenantA);
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.AddRange([t1, t2]);

        var artifacts = Substitute.For<IDiscoveryArtifactStore>();
        var upload = new DiscoveryArtifacts(t1.Table.Id, TenantA, null, null, "upload-sha-1", "admin", DateTimeOffset.UtcNow, Provenance: ArtifactProvenance.Upload);
        var import = new DiscoveryArtifacts(t1.Table.Id, TenantA, null, null, "import-sha-1", "admin", DateTimeOffset.UtcNow, Provenance: ArtifactProvenance.Import);
        artifacts.GetAllForTablesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<DiscoveryArtifacts>> { [t1.Table.Id] = [import, upload] });

        var protection = Substitute.For<IProtectionSnapshotProvider>();
        protection.ResolveManyAsync(new TenantId(TenantA), Arg.Any<IReadOnlyList<TableMetadata>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<TableIdentifier, EffectiveProtection>
            {
                [t1.Identifier] = new EffectiveProtection(new ContainerRef(ContainerKind.Domain, Guid.Empty), null, ProtectionClass.Confidential, ProtectionClass.Confidential, null, new Dictionary<string, ColumnProtection>(), CatalogVisibilityMode.OnRequest),
                [t2.Identifier] = new EffectiveProtection(new ContainerRef(ContainerKind.Domain, Guid.Empty), null, ProtectionClass.Internal, ProtectionClass.Internal, null, new Dictionary<string, ColumnProtection>(), CatalogVisibilityMode.OnRequest)
            });

        var changeStore = new InMemoryCatalogChangeStore();
        var options = Options.Create(new GatewayOptions { Tenancy = new TenancyOptions { DefaultTenantId = null } });
        var indexer = new CatalogChangeIndexerHostedService(
            repo, new FakeDiscoveryConsentRepository(), new FakeDiscoveryOwnershipRepository(),
            changeStore, options, artifacts, protectionProvider: protection);

        var written = await indexer.IndexTenantAsync(new TenantId(TenantA), [t1, t2]);
        written.ShouldBe(2);

        await artifacts.Received(1).GetAllForTablesAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2), Arg.Any<CancellationToken>());
        await artifacts.DidNotReceive().GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await protection.Received(1).ResolveManyAsync(new TenantId(TenantA), Arg.Is<IReadOnlyList<TableMetadata>>(m => m.Count == 2), Arg.Any<CancellationToken>());
        await protection.DidNotReceive().GetAsync(Arg.Any<TenantId>(), Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>());
    }

    private sealed class FakeEpochService : IEpochValidationService
    {
        public long? InvalidationGeneration { get; set; }
        public long? ClusterSequence { get; set; }

        public ValueTask<long?> GetClusterInvalidationSequenceAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(ClusterSequence);

        public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(1L);

        public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(tables.Distinct().ToDictionary(static t => t, static _ => 1L));
    }

    private sealed class StubGovernanceNotifier : IGovernanceChangeNotifier
    {
        private readonly List<Action<GovernanceChangeEvent>> _handlers = [];

        public Task NotifyAsync(GovernanceChangeEvent change, CancellationToken ct = default)
        {
            Emit(change);
            return Task.CompletedTask;
        }

        public void Emit(GovernanceChangeEvent evt)
        {
            foreach (var h in _handlers)
            {
                h(evt);
            }
        }

        public IDisposable Subscribe(Action<GovernanceChangeEvent> handler)
        {
            _handlers.Add(handler);
            return new Sub(() => _handlers.Remove(handler));
        }

        private sealed class Sub(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
