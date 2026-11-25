namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.External;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Catalog.Visibility;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog.Discovery;
using Autheris.Tests.Unit.Governance;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ExternalCatalogContainerVisibilityTests
{
    [Fact]
    public async Task GetChanges_UnauthorizedCallers_DoNotLeakDiscoveryHiddenAsRemoved()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.RegisterExternalSystem("tenant-a", "catalog-sync-admin", enabled: true);

        var publicTable = harness.AddTable("pg", "orders", ownerTenantId: "tenant-a", hidden: false);
        var hiddenTable = harness.AddTable("pg", "secret_audit", ownerTenantId: "tenant-a", hidden: true);
        await harness.CreateIndexer().IndexOnceAsync();

        var person = ExternalCatalogTestHarness.BuildDelegatedUserPrincipal("tenant-a", "S-1-5-21-PERSON", scopes: "Apps.Read");
        var appReader = ExternalCatalogTestHarness.BuildAppPrincipal("tenant-a", "regular-app", ExternalApiPermissions.CatalogRead);
        var appAdmin = ExternalCatalogTestHarness.BuildAppPrincipal(
            "tenant-a",
            "catalog-sync-admin",
            ExternalApiPermissions.CatalogRead,
            ExternalApiPermissions.CatalogReadAdmin);

        foreach (var caller in new[] { person, appReader })
        {
            foreach (var since in new[] { 0L, 1L })
            {
                var feed = await harness.GetChangesAsync(caller, since: since);
                feed.StatusCode.ShouldBe(StatusCodes.Status200OK);
                var items = feed.ReadJson().GetProperty("changes").EnumerateArray().ToList();
                items.Select(c => c.GetProperty("objectId").GetString())
                    .ShouldNotContain(hiddenTable.Identifier.ToString());
                items.Where(c => c.GetProperty("kind").GetString() == "removed")
                    .ShouldBeEmpty();
            }
        }

        var adminFeed = await harness.GetChangesAsync(appAdmin, since: 0);
        adminFeed.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var adminItems = adminFeed.ReadJson().GetProperty("changes").EnumerateArray().ToList();
        var ids = adminItems.Select(c => c.GetProperty("objectId").GetString()).ToList();
        ids.ShouldContain(publicTable.Identifier.ToString());
        ids.ShouldContain(hiddenTable.Identifier.ToString());
    }

    [Fact]
    public async Task GetChanges_CrossTenantOwnerTable_IsExcludedForAllCallers()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.RegisterExternalSystem("tenant-a", "catalog-sync-admin", enabled: true);

        var tenantATable = harness.AddTable("pg", "orders", ownerTenantId: "tenant-a", hidden: false);
        var tenantBTable = harness.AddTable("pg", "foreign_orders", ownerTenantId: "tenant-b", hidden: false);
        await harness.ChangeStore.ApplyVersionChangesAsync(
            new TenantId("tenant-a"),
            [
                new CatalogVersionUpsert(tenantATable.Identifier.ToString(), "sha-a", Removed: false),
                new CatalogVersionUpsert(tenantBTable.Identifier.ToString(), "sha-b", Removed: false)
            ],
            ExternalCatalogTestHarness.FixedNow);

        var appAdmin = ExternalCatalogTestHarness.BuildAppPrincipal(
            "tenant-a",
            "catalog-sync-admin",
            ExternalApiPermissions.CatalogRead,
            ExternalApiPermissions.CatalogReadAdmin);

        var feed = await harness.GetChangesAsync(appAdmin, since: 0);
        feed.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var ids = feed.ReadJson().GetProperty("changes").EnumerateArray()
            .Select(c => c.GetProperty("objectId").GetString())
            .ToList();
        ids.ShouldContain(tenantATable.Identifier.ToString());
        ids.ShouldNotContain(tenantBTable.Identifier.ToString());
    }

    [Fact]
    public async Task GetObject_AppOnly_EnforcesContainerVisibility_AndGrants()
    {
        using var h = new ContainerHarness();
        var (openTable, closedTable, grantedTable, unassignedTable) = h.SeedStandardTables();

        var grantedSp = BuildBoundAppPrincipal("tenant-a", "catalog-sync-admin", "default|catalog-sync-admin",
            ExternalApiPermissions.CatalogRead, ExternalApiPermissions.CatalogReadAdmin);
        var unboundSp = ExternalCatalogTestHarness.BuildAppPrincipal("tenant-a", "catalog-sync-admin",
            ExternalApiPermissions.CatalogRead, ExternalApiPermissions.CatalogReadAdmin);

        (await h.Reader.GetObjectAsync(grantedSp, h.Tenant, openTable.Identifier.ToString())).IsSuccess.ShouldBeTrue();
        (await h.Reader.GetObjectAsync(grantedSp, h.Tenant, grantedTable.Identifier.ToString())).IsSuccess.ShouldBeTrue();
        (await h.Reader.GetObjectAsync(grantedSp, h.Tenant, closedTable.Identifier.ToString())).Error!.StatusCode.ShouldBe(404);
        (await h.Reader.GetObjectAsync(grantedSp, h.Tenant, unassignedTable.Identifier.ToString())).Error!.StatusCode.ShouldBe(404);

        (await h.Reader.GetObjectAsync(unboundSp, h.Tenant, openTable.Identifier.ToString())).IsSuccess.ShouldBeTrue();
        (await h.Reader.GetObjectAsync(unboundSp, h.Tenant, grantedTable.Identifier.ToString())).Error!.StatusCode.ShouldBe(404);
        (await h.Reader.GetObjectAsync(unboundSp, h.Tenant, closedTable.Identifier.ToString())).Error!.StatusCode.ShouldBe(404);
        (await h.Reader.GetObjectAsync(unboundSp, h.Tenant, unassignedTable.Identifier.ToString())).Error!.StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task ListObjects_And_Facets_AppOnly_EnforceContainerVisibility()
    {
        using var h = new ContainerHarness();
        var (openTable, closedTable, grantedTable, unassignedTable) = h.SeedStandardTables();

        var grantedSp = BuildBoundAppPrincipal("tenant-a", "catalog-sync-admin", "default|catalog-sync-admin",
            ExternalApiPermissions.CatalogRead, ExternalApiPermissions.CatalogReadAdmin);

        var list = await h.Reader.ListObjectsAsync(grantedSp, h.Tenant, new ExternalCatalogListQuery());
        list.IsSuccess.ShouldBeTrue();
        var listIds = list.Value!.Items.Select(i => i.ObjectId).ToList();
        listIds.ShouldContain(openTable.Identifier.ToString());
        listIds.ShouldContain(grantedTable.Identifier.ToString());
        listIds.ShouldNotContain(closedTable.Identifier.ToString());
        listIds.ShouldNotContain(unassignedTable.Identifier.ToString());
        list.Value.TotalCount.ShouldBe(2);

        var facets = await h.Reader.GetFacetsAsync(grantedSp, h.Tenant);
        facets.IsSuccess.ShouldBeTrue();
        facets.Value!.TotalCount.ShouldBe(2);
        var facetDomains = facets.Value.Domains.Select(d => d.Domain).ToList();
        facetDomains.ShouldNotContain(closedTable.Identifier.Domain);
        facetDomains.ShouldNotContain(unassignedTable.Identifier.Domain);
    }

    [Fact]
    public async Task GetChanges_PersonAndAppOnly_EnforceContainerVisibility()
    {
        using var h = new ContainerHarness();
        var (openTable, closedTable, grantedTable, unassignedTable) = h.SeedStandardTables();
        await h.ChangeStore.ApplyVersionChangesAsync(
            h.Tenant,
            [
                new CatalogVersionUpsert(openTable.Identifier.ToString(), "h1", Removed: false),
                new CatalogVersionUpsert(closedTable.Identifier.ToString(), "h2", Removed: false),
                new CatalogVersionUpsert(grantedTable.Identifier.ToString(), "h3", Removed: false),
                new CatalogVersionUpsert(unassignedTable.Identifier.ToString(), "h4", Removed: false)
            ],
            h.Time.GetUtcNow());

        var grantedSp = BuildBoundAppPrincipal("tenant-a", "catalog-sync-admin", "default|catalog-sync-admin",
            ExternalApiPermissions.CatalogRead, ExternalApiPermissions.CatalogReadAdmin);
        var person = ExternalCatalogTestHarness.BuildDelegatedUserPrincipal("tenant-a", "S-1-5-21-USER", scopes: "Apps.Read");

        var spFeed = await h.Reader.GetChangesAsync(grantedSp, h.Tenant, sinceSeq: 0, limit: 50);
        spFeed.IsSuccess.ShouldBeTrue();
        var spIds = spFeed.Value!.Changes.Select(c => c.ObjectId).ToList();
        spIds.ShouldContain(openTable.Identifier.ToString());
        spIds.ShouldContain(grantedTable.Identifier.ToString());
        spIds.ShouldNotContain(closedTable.Identifier.ToString());
        spIds.ShouldNotContain(unassignedTable.Identifier.ToString());

        var personFeed = await h.Reader.GetChangesAsync(person, h.Tenant, sinceSeq: 0, limit: 50);
        personFeed.IsSuccess.ShouldBeTrue();
        var personIds = personFeed.Value!.Changes.Select(c => c.ObjectId).ToList();
        personIds.ShouldContain(openTable.Identifier.ToString());
        personIds.ShouldNotContain(grantedTable.Identifier.ToString());
        personIds.ShouldNotContain(closedTable.Identifier.ToString());
        personIds.ShouldNotContain(unassignedTable.Identifier.ToString());
    }

    [Fact]
    public async Task AppOnly_And_GetChanges_FailClosed_WhenContainerVisibilityUnavailable()
    {
        using var h = new ContainerHarness();
        var (openTable, _, _, _) = h.SeedStandardTables();
        await h.ChangeStore.ApplyVersionChangesAsync(
            h.Tenant,
            [new CatalogVersionUpsert(openTable.Identifier.ToString(), "h1", Removed: false)],
            h.Time.GetUtcNow());
        h.Domains.ListFailure = new InvalidOperationException("container store down");

        var sp = BuildBoundAppPrincipal("tenant-a", "catalog-sync-admin", "default|catalog-sync-admin",
            ExternalApiPermissions.CatalogRead, ExternalApiPermissions.CatalogReadAdmin);

        (await h.Reader.GetObjectAsync(sp, h.Tenant, openTable.Identifier.ToString())).Error!.StatusCode.ShouldBe(404);
        var list = await h.Reader.ListObjectsAsync(sp, h.Tenant, new ExternalCatalogListQuery());
        list.IsSuccess.ShouldBeTrue();
        list.Value!.Items.ShouldBeEmpty();
        var facets = await h.Reader.GetFacetsAsync(sp, h.Tenant);
        facets.IsSuccess.ShouldBeTrue();
        facets.Value!.TotalCount.ShouldBe(0);
        var changes = await h.Reader.GetChangesAsync(sp, h.Tenant, sinceSeq: 0, limit: 50);
        changes.IsSuccess.ShouldBeTrue();
        changes.Value!.Changes.ShouldBeEmpty();
    }

    private static ClaimsPrincipal BuildBoundAppPrincipal(string tenantId, string clientId, string sid, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new("idtyp", "app"),
            new("azp", clientId),
            new("tenant_id", tenantId),
            new(ClaimTypes.PrimarySid, sid),
            new(ClaimTypes.NameIdentifier, sid),
            GroupResolutionClaims.CreateMarker(),
            GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim("roles", role));
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test", ClaimTypes.NameIdentifier, ClaimTypes.Role));
    }

    private sealed class ContainerHarness : IDisposable
    {
        public TenantId Tenant { get; } = new("tenant-a");
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
        public FakeDiscoveryTableRepository Tables { get; } = new();
        public FakeContainerDomainRepository Domains { get; } = new();
        public FakeCatalogGrantRepository Grants { get; } = new();
        public FakeConsentRepository Consents { get; } = new();
        public InMemoryCatalogChangeStore ChangeStore { get; } = new();
        public InMemoryExternalWorkflowRepository WorkflowRepo { get; } = new();
        public GovernanceEpochProvider Epochs { get; }
        public ContainerVisibilitySnapshotProvider VisibilityProvider { get; }
        public StubProtectionMapProvider ProtectionProvider { get; } = new();
        public ExternalCatalogReader Reader { get; }

        public ContainerHarness()
        {
            var notifier = new NoopChangeNotifier();
            Epochs = new GovernanceEpochProvider(
                Domains,
                Options.Create(new GovernanceCacheSettings { EpochPollSeconds = 60 }),
                notifier,
                Time);
            VisibilityProvider = new ContainerVisibilitySnapshotProvider(
                Domains,
                Grants,
                Consents,
                new FakeGovernanceCapabilities(),
                Epochs,
                Options.Create(new GovernanceCacheSettings { MaxTtlSeconds = 300, NegativeTtlSeconds = 60, EpochPollSeconds = 60 }),
                notifier,
                Time);

            var gatewayOptions = Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() });
            var discoveryOptions = Options.Create(new CatalogDiscoveryOptions());
            var discovery = new DiscoveryService(
                Tables,
                FakeAccessDecisionPoint.DenyAll(),
                Consents,
                new FakeDiscoveryApprovalRepository(),
                new FakeDiscoveryOwnershipRepository(),
                new FakeDiscoveryArtifactStore(),
                new FakeAuditWriter(),
                gatewayOptions,
                discoveryOptions,
                logger: NullLogger<DiscoveryService>.Instance,
                protectionProvider: ProtectionProvider,
                containerVisibility: VisibilityProvider);

            WorkflowRepo.Systems.Add(new ExternalWorkflowSystem(
                Guid.NewGuid(),
                Tenant.Value,
                "catalog-sync-admin",
                "catalog-sync-admin",
                "https://example.invalid/hook",
                "secret-ref",
                true,
                "admin",
                Time.GetUtcNow(),
                null));

            Reader = new ExternalCatalogReader(
                Tables,
                discovery,
                Consents,
                new FakeDiscoveryOwnershipRepository(),
                WorkflowRepo,
                ChangeStore,
                gatewayOptions,
                timeProvider: Time,
                protectionProvider: ProtectionProvider,
                containerVisibility: VisibilityProvider);
        }

        public (TableMetadata Open, TableMetadata Closed, TableMetadata Granted, TableMetadata Unassigned) SeedStandardTables()
        {
            var openDom = Domains.Add(Tenant, "dom_open", CatalogVisibilityMode.Open);
            var closedDom = Domains.Add(Tenant, "dom_closed", CatalogVisibilityMode.OnRequest);
            var grantedDom = Domains.Add(Tenant, "dom_granted", CatalogVisibilityMode.OnRequest);
            var unassignedDom = Domains.Add(Tenant, "dom_unassigned", CatalogVisibilityMode.Open, unassigned: true);

            Grants.Add(Tenant, new ContainerRef(ContainerKind.Domain, grantedDom.Id), new Sid("default|catalog-sync-admin"));

            var openTable = Add("dom_open", "t_open", openDom, CatalogVisibilityMode.Open);
            var closedTable = Add("dom_closed", "t_closed", closedDom, CatalogVisibilityMode.OnRequest);
            var grantedTable = Add("dom_granted", "t_granted", grantedDom, CatalogVisibilityMode.OnRequest);
            var unassignedTable = Add("dom_unassigned", "t_unassigned", unassignedDom, CatalogVisibilityMode.Open);
            return (openTable, closedTable, grantedTable, unassignedTable);
        }

        private TableMetadata Add(string domain, string name, GovernanceDomain dom, CatalogVisibilityMode mode)
        {
            var t = DiscoveryTestBuilder.MakeTable(domain, name, ownerTenantId: Tenant.Value);
            Tables.Tables.Add(t);
            ProtectionProvider.Map[t.Identifier] = new EffectiveProtection(
                new ContainerRef(ContainerKind.Domain, dom.Id),
                null,
                ProtectionClass.Internal,
                ProtectionClass.Internal,
                null,
                new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase),
                mode);
            return t;
        }

        public void Dispose()
        {
            VisibilityProvider.Dispose();
            Epochs.Dispose();
        }
    }

    private sealed class StubProtectionMapProvider : IProtectionSnapshotProvider
    {
        public Dictionary<TableIdentifier, EffectiveProtection> Map { get; } = [];

        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(Map.TryGetValue(table, out var ep)
                ? ep
                : new EffectiveProtection(
                    new ContainerRef(ContainerKind.Domain, Guid.Empty),
                    null,
                    ProtectionClass.Internal,
                    ProtectionClass.Internal,
                    null,
                    new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase),
                    CatalogVisibilityMode.OnRequest));
    }

    private sealed class NoopChangeNotifier : IGovernanceChangeNotifier
    {
        public IDisposable Subscribe(Action<GovernanceChangeEvent> handler) => new NoopDisposable();
        public Task NotifyAsync(GovernanceChangeEvent change, CancellationToken ct = default) => Task.CompletedTask;
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
