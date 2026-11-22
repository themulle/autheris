namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class CatalogPagingScaleTests
{
    private sealed class CountingTableRepository(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        public int GetAllCalls { get; private set; }
        public int QueryTablesCalls { get; private set; }
        public int GetSingleCalls { get; private set; }
        public List<CatalogTableQuery> Queries { get; } = [];

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
        {
            GetSingleCalls++;
            return Task.FromResult(tables.FirstOrDefault(t => t.Identifier.Equals(table)));
        }

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
        {
            GetAllCalls++;
            return Task.FromResult(tables);
        }

        public Task<CatalogTablePage> QueryTablesAsync(CatalogTableQuery query, CancellationToken ct = default)
        {
            QueryTablesCalls++;
            Queries.Add(query);
            query.EnsureValid(out var domain, out var schema, out var namePrefix, out var after);
            var limit = query.EffectiveLimit;
            var matched = tables
                .Where(t => query.IncludeInactive || t.Table.IsActive)
                .Where(t => domain is null || string.Equals(t.Identifier.Domain, domain, StringComparison.OrdinalIgnoreCase))
                .Where(t => schema is null || string.Equals(t.Identifier.Schema, schema, StringComparison.OrdinalIgnoreCase))
                .Where(t => namePrefix is null || t.Identifier.TableName.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                .Where(t => after is null || CatalogTableCursor.Compare(t.Identifier, after.Value) > 0)
                .OrderBy(t => t.Identifier.Domain, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Identifier.Schema, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.Identifier.TableName, StringComparer.OrdinalIgnoreCase)
                .Take(limit + 1)
                .ToList();

            var hasMore = matched.Count > limit;
            var items = hasMore ? matched.Take(limit).ToList() : matched;
            var nextAfter = hasMore ? CatalogTableCursor.Encode(items[^1].Identifier) : null;
            return Task.FromResult(new CatalogTablePage(items, nextAfter));
        }

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<long> FlushCatalogVersionAsync(CancellationToken ct = default) => ValueTask.FromResult(1L);
    }

    private sealed class StubSnapshotProvider(CatalogSnapshot snapshot) : ICatalogSnapshotProvider
    {
        public int GetSnapshotCalls { get; private set; }
        public CatalogSnapshot CurrentSnapshot => snapshot;
        public bool IsInitialized => true;
        public bool IsDegraded => false;

        public ValueTask<CatalogSnapshot> GetSnapshotAsync(CancellationToken ct = default)
        {
            GetSnapshotCalls++;
            return ValueTask.FromResult(snapshot);
        }

        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void SignalChange() { }
    }

    private sealed class StubProcedureCatalog(IReadOnlyList<RegisteredProcedure> entries) : IProcedureCatalog
    {
        public Task<IReadOnlyList<RegisteredProcedure>> ListVisibleAsync(ClaimsPrincipal user, TenantId tenantId, CancellationToken ct)
            => Task.FromResult(entries);

        public Task<RegisteredProcedure?> FindVisibleAsync(string name, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct)
            => Task.FromResult(entries.FirstOrDefault(e => string.Equals(e.Definition.Name, name, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed class NoopExecutionService : IGatewayExecutionService
    {
        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(ClaimsPrincipal? principal, TableIdentifier table, int? first = null, int? after = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(ClaimsPrincipal? principal, TableIdentifier table, int? first, int? after, IReadOnlyDictionary<string, object?>? queryArguments, IReadOnlyList<string>? requestedFields = null, IReadOnlyDictionary<string, string[]>? requestHeaders = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TableAccessDecision> CheckTableAccessAsync(ClaimsPrincipal? principal, TableIdentifier table, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static List<TableMetadata> MakeTables(int count, int domainBuckets = 1)
    {
        var list = new List<TableMetadata>(count);
        for (var i = 0; i < count; i++)
        {
            var domain = $"d{i % domainBuckets:00}";
            list.Add(DiscoveryTestBuilder.MakeTable(domain, $"t{i:0000}", ownerTenantId: "tenant-a"));
        }

        return list;
    }

    private static DiscoveryService CreateDiscoveryService(
        ITableMetadataRepository repo,
        IAccessDecisionPoint? pdp = null,
        IConsentRepository? consents = null,
        ICatalogSnapshotProvider? snapshotProvider = null,
        IProcedureCatalog? procedureCatalog = null)
        => new(
            repo,
            pdp ?? FakeAccessDecisionPoint.AllowAll(),
            consents ?? new FakeDiscoveryConsentRepository(),
            new FakeDiscoveryApprovalRepository(),
            new FakeDiscoveryOwnershipRepository(),
            new FakeDiscoveryArtifactStore(),
            new FakeAuditWriter(),
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            Options.Create(new CatalogDiscoveryOptions()),
            procedureCatalog: procedureCatalog,
            logger: NullLogger<DiscoveryService>.Instance,
            snapshotProvider: snapshotProvider);

    [Fact]
    public async Task DiscoveryService_WithSnapshotProvider_DoesNotReadRepository_OnListGetFacetsOrKeyset()
    {
        var tables = MakeTables(300, domainBuckets: 3);
        var repo = new CountingTableRepository(tables);
        var snapshot = CatalogSnapshot.Create(new CatalogVersion(1, 1), tables, []);
        var snapshots = new StubSnapshotProvider(snapshot);
        var service = CreateDiscoveryService(repo, snapshotProvider: snapshots);
        var caller = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-USER", "tenant-a"));

        var list = await service.ListAsync(new DiscoveryQuery(First: 25, After: 50), caller);
        var fetched = await service.GetAsync(tables[10].Identifier, caller);
        var facets = await service.FacetsAsync(access: null, caller);
        var keyset = await service.ListKeysetAsync(new DiscoveryKeysetQuery(First: 20), caller);

        list.Items.Count.ShouldBe(25);
        list.TotalCount.ShouldBe(300);
        fetched.ShouldNotBeNull();
        facets.TotalCount.ShouldBe(300);
        facets.Domains.Count.ShouldBe(3);
        keyset.Items.Count.ShouldBe(20);

        snapshots.GetSnapshotCalls.ShouldBeGreaterThan(0);
        repo.GetAllCalls.ShouldBe(0);
        repo.QueryTablesCalls.ShouldBe(0);
        repo.GetSingleCalls.ShouldBe(0);
    }

    [Fact]
    public async Task DiscoveryService_WithoutSnapshotProvider_GetAsync_ReadsOnlySingleTable()
    {
        var tables = MakeTables(50);
        var repo = new CountingTableRepository(tables);
        var service = CreateDiscoveryService(repo);
        var caller = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-USER", "tenant-a"));

        var fetched = await service.GetAsync(tables[25].Identifier, caller);

        fetched.ShouldNotBeNull();
        repo.GetSingleCalls.ShouldBe(1);
        repo.GetAllCalls.ShouldBe(0);
        repo.QueryTablesCalls.ShouldBe(0);
    }

    [Fact]
    public async Task DiscoveryService_WithoutSnapshotProvider_DomainFilteredQueries_PushDomainToRepository()
    {
        var tables = MakeTables(100, domainBuckets: 4);
        var repo = new CountingTableRepository(tables);
        var service = CreateDiscoveryService(repo);
        var caller = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-USER", "tenant-a"));

        var list = await service.ListAsync(new DiscoveryQuery(Domain: "d01", First: 10), caller);
        var keyset = await service.ListKeysetAsync(new DiscoveryKeysetQuery(Domain: "d02", First: 10), caller);

        list.TotalCount.ShouldBe(25);
        keyset.Items.Count.ShouldBe(10);
        repo.GetAllCalls.ShouldBe(0);
        repo.Queries.ShouldContain(q => string.Equals(q.DataSource, "d01", StringComparison.OrdinalIgnoreCase));
        repo.Queries.ShouldContain(q => string.Equals(q.DataSource, "d02", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task McpDatasetCatalog_ListDatasets_WithProcedureCatalog_PagesAbove200AndBoundsPdpCallsToPageSize()
    {
        var tables = MakeTables(400, domainBuckets: 2);
        var repo = new CountingTableRepository(tables);
        var snapshot = CatalogSnapshot.Create(new CatalogVersion(1, 1), tables, []);
        var snapshots = new StubSnapshotProvider(snapshot);
        var pdp = FakeAccessDecisionPoint.AllowAll();

        var procDef = new ProcedureDefinition(
            Name: "hr_proc",
            Summary: "HR summary",
            ProcedureName: "dbo.hr_proc",
            Mode: ProcedureMode.Read,
            DataSource: "hr",
            Parameters: [],
            ContextBindings: [],
            RlsMode: ProcedureRlsMode.SessionContext,
            ResultTable: "dbo.t0000",
            ClearedResultColumns: [],
            RequiredRoles: [],
            TimeoutSeconds: 30);
        var proc = new RegisteredProcedure(
            procDef,
            ProcedureState.Active,
            new ProcedureValidationResult(true, [], [], ["dbo.t0000"], new Dictionary<string, string>()),
            DateTimeOffset.UtcNow,
            null);
        var procCatalog = new StubProcedureCatalog([proc]);

        var discovery = CreateDiscoveryService(repo, pdp: pdp, snapshotProvider: snapshots, procedureCatalog: procCatalog);
        var mcp = new McpDatasetCatalog(
            repo,
            new FakeDiscoveryConsentRepository(),
            new NoopExecutionService(),
            procedureCatalog: procCatalog,
            discoveryService: discovery);

        var user = DiscoveryTestBuilder.User("S-USER", "tenant-a");

        // Filter to domain d00 (200 tables, 0 matching procedures): should page cleanly and only decide 25 tables (4 ops each = 100).
        var domainPage = await mcp.ListDatasetsAsync(user, search: null, domain: "d00", offset: 150, limit: 25);
        domainPage.Total.ShouldBe(200);
        domainPage.Datasets.Count.ShouldBe(25);
        pdp.CallCount.ShouldBe(25 * 4);

        // Unfiltered page above offset 200 (400 tables + 1 procedure = 401): must not truncate at 200.
        var deepPage = await mcp.ListDatasetsAsync(user, search: null, domain: null, offset: 250, limit: 25);
        deepPage.Total.ShouldBe(401);
        deepPage.Datasets.Count.ShouldBe(25);
        repo.GetAllCalls.ShouldBe(0);
    }

    [Fact]
    public async Task McpDatasetCatalog_ListDatasets_Overview_UsesFacetsWithoutProjectingObjects()
    {
        var tables = MakeTables(350, domainBuckets: 5);
        var repo = new CountingTableRepository(tables);
        var snapshot = CatalogSnapshot.Create(new CatalogVersion(1, 1), tables, []);
        var snapshots = new StubSnapshotProvider(snapshot);
        var pdp = FakeAccessDecisionPoint.AllowAll();
        var discovery = CreateDiscoveryService(repo, pdp: pdp, snapshotProvider: snapshots);
        var mcp = new McpDatasetCatalog(
            repo,
            new FakeDiscoveryConsentRepository(),
            new NoopExecutionService(),
            discoveryService: discovery);

        var user = DiscoveryTestBuilder.User("S-USER", "tenant-a");
        var overview = await mcp.ListDatasetsAsync(
            user,
            search: null,
            domain: null,
            offset: null,
            limit: null,
            tag: null,
            language: null,
            overview: true,
            format: null,
            accessStatus: null,
            ct: CancellationToken.None);

        overview.Total.ShouldBe(350);
        overview.Domains.ShouldNotBeNull();
        overview.Domains!.Count.ShouldBe(5);
        pdp.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task McpDatasetCatalog_ListDatasets_ReadsConsentsBoundedByPageSize()
    {
        var tables = MakeTables(500);
        var repo = new CountingTableRepository(tables);
        var snapshot = CatalogSnapshot.Create(new CatalogVersion(1, 1), tables, []);
        var snapshots = new StubSnapshotProvider(snapshot);
        var consents = new FakeDiscoveryConsentRepository();
        foreach (var t in tables)
        {
            consents.Active.Add(new Consent
            {
                Id = Guid.NewGuid(),
                GranteeSid = new Sid("S-USER"),
                TenantId = new TenantId("tenant-a"),
                TableIdentifier = t.Identifier,
                Effect = ConsentEffect.Allow,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1)
            });
        }

        var singleTableConsentReads = 0;
        var pdp = new FakeAccessDecisionPoint(req =>
        {
            if (req.Operation == AccessOperation.Discover)
            {
                singleTableConsentReads++;
            }

            return TableAccessDecision.Allowed(req.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        });

        var discovery = CreateDiscoveryService(repo, pdp: pdp, consents: consents, snapshotProvider: snapshots);
        var mcp = new McpDatasetCatalog(
            repo,
            consents,
            new NoopExecutionService(),
            discoveryService: discovery);

        var user = DiscoveryTestBuilder.User("S-USER", "tenant-a");
        var page = await mcp.ListDatasetsAsync(user, search: null, domain: null, offset: 100, limit: 40);

        page.Total.ShouldBe(500);
        page.Datasets.Count.ShouldBe(40);
        consents.AllActiveReads.ShouldBe(0);
        singleTableConsentReads.ShouldBe(40);
    }

    [Fact]
    public async Task CatalogDiscoveryService_ListDatasets_Above200_ReturnsAllPages_AndFeedsMcpSummaryAndApiDispatcher()
    {
        var tables = MakeTables(250, domainBuckets: 5);
        var repo = new CountingTableRepository(tables);
        var snapshot = CatalogSnapshot.Create(new CatalogVersion(1, 1), tables, []);
        var snapshots = new StubSnapshotProvider(snapshot);
        var discovery = CreateDiscoveryService(repo, snapshotProvider: snapshots);

        var catalogService = new CatalogDiscoveryService(
            repo,
            new FakeAuditWriter(),
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            logger: NullLogger<CatalogDiscoveryService>.Instance,
            discoveryService: discovery);

        var user = DiscoveryTestBuilder.User("S-USER", "tenant-a");
        var ctx = new RequestContext(new TenantId("tenant-a"), new Sid("S-USER"), [], null, user);

        var allDatasets = await catalogService.ListDatasetsAsync(ctx);
        allDatasets.Count.ShouldBe(250);

        var dispatcher = new ApiDispatcherService(catalogDiscoveryService: catalogService);
        var apiJson = await dispatcher.InvokeApiAsync("/api/v1/catalog/datasets", "GET", context: ctx);
        using var doc = System.Text.Json.JsonDocument.Parse(apiJson);
        doc.RootElement.GetProperty("count").GetInt32().ShouldBe(250);
        doc.RootElement.GetProperty("datasets").GetArrayLength().ShouldBe(250);

        var provider = new McpResourceProvider(catalogService, dispatcher);
        var session = new McpSessionContext("s-1", "sp-1", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-USER");
        var summary = await provider.ReadAsync("autheris://catalog/summary", session);
        summary.Text.ShouldContain("Total Datasets: 250 across 5 business domains.");
    }
}
