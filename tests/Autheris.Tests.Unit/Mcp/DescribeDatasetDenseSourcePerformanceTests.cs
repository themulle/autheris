namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Mcp;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Catalog.Discovery;
using Autheris.Tests.Unit.TestSupport;
using HotChocolate.Execution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class DescribeDatasetDenseSourcePerformanceTests
{
    private const string Tenant = "tenant-a";
    private const string UserSid = "S-1-USER";

    [Fact]
    public async Task DescribeDataset_DenseSource_ZeroExamples_SkipsTransitiveRelationWalk()
    {
        var harness = DenseHarness.Create(hubRelationCount: 150, visibleChildCount: 20, transitiveNeighborCount: 300, goldenQueries: []);

        var desc = await harness.Catalog.DescribeDatasetAsync(harness.Principal, DenseHarness.HubIdString, language: null, brief: false);

        desc.Examples.ShouldBeEmpty();
        desc.GraphQl.ShouldNotBeNull();
        desc.GraphQl!.Relations.Count.ShouldBe(20);
        desc.GraphQl.Relations.All(r => r.Columns is { Count: 1 }).ShouldBeTrue();

        harness.MetadataRepo.SingleReadCount.ShouldBe(0);
        harness.VisibilitySpy.FindManyCalls.ShouldBe(1);
        harness.ConsentRepo.AllActiveCalls.ShouldBeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task DescribeDataset_DenseSource_TwoHopExample_ResolvesOnlyReferencedTargets()
    {
        var child0 = new TableIdentifier("dense", "dbo", "rel_000");
        var grandChild = new TableIdentifier("dense", "dbo", "leaf_000");
        var queryText = "{ dense_dbo_hub { id rel_000 { name leaf_000 { code } } } }";
        var record = new GoldenQueryStoreRecord(
            "gq-1", "t-hub", "dense", "dbo", "hub", GoldenQuerySource.Dbt, "gq-1", "Hub Example", null, queryText, null, new string('a', 64), DateTimeOffset.UtcNow);
        var harness = DenseHarness.Create(
            hubRelationCount: 150,
            visibleChildCount: 10,
            transitiveNeighborCount: 200,
            goldenQueries: [record],
            extraVisibleTables: [grandChild]);

        var desc = await harness.Catalog.DescribeDatasetAsync(harness.Principal, DenseHarness.HubIdString, language: null, brief: false);

        desc.Examples.ShouldHaveSingleItem().Title.ShouldBe("Hub Example");
        // Example resolution batches only [child0] then [grandChild]; neither leaf_001..leaf_199 nor unrelated rel_001..rel_149 are touched for examples.
        harness.VisibilitySpy.ExamplePhaseBatchedTargets.ShouldBe([child0, grandChild], ignoreOrder: true);
        harness.VisibilitySpy.BatchSizes.Take(2).ShouldBe([1, 1]);
        harness.MetadataRepo.SingleReadCount.ShouldBe(0);
    }

    [Fact]
    public async Task DescribeDataset_IncludeColumnsOnly_SkipsExamplesAndGraphQlBeforeBuild()
    {
        var golden = new GoldenQueryStoreRecord(
            "gq-1", "t-hub", "dense", "dbo", "hub", GoldenQuerySource.Dbt, "gq-1", "Hub Example", null, "{ dense_dbo_hub { id } }", null, new string('a', 64), DateTimeOffset.UtcNow);
        var harness = DenseHarness.Create(hubRelationCount: 80, visibleChildCount: 10, transitiveNeighborCount: 50, goldenQueries: [golden], withDiscovery: true);
        var executor = harness.CreateExecutor();

        var json = await executor.ExecuteOperationAsync(
            DescribeTool(),
            $$"""{"dataset":"{{DenseHarness.HubIdString}}","include":["columns"]}""",
            Session());

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("columns").GetArrayLength().ShouldBeGreaterThan(0);
        doc.RootElement.TryGetProperty("graphQl", out _).ShouldBeFalse();
        doc.RootElement.GetProperty("examples").GetArrayLength().ShouldBe(0);
        harness.GoldenRepo.GetForTenantCalls.ShouldBe(0);
        harness.VisibilitySpy.FindManyCalls.ShouldBe(0);
        harness.ConsentRepo.AllActiveCalls.ShouldBe(0);
    }

    [Fact]
    public async Task DescribeDataset_IncludeGraphQlWithoutRelations_SkipsRelationTargetLookups()
    {
        var harness = DenseHarness.Create(hubRelationCount: 80, visibleChildCount: 10, transitiveNeighborCount: 50, goldenQueries: [], withDiscovery: true);
        var executor = harness.CreateExecutor();

        var json = await executor.ExecuteOperationAsync(
            DescribeTool(),
            $$"""{"dataset":"{{DenseHarness.HubIdString}}","include":["columns","graphql"]}""",
            Session());

        using var doc = JsonDocument.Parse(json);
        var gql = doc.RootElement.GetProperty("graphQl");
        gql.GetProperty("queryField").GetString().ShouldBe("dense_dbo_hub");
        gql.GetProperty("exampleQuery").GetString().ShouldNotBeNullOrWhiteSpace();
        gql.GetProperty("relations").GetArrayLength().ShouldBe(0);
        harness.VisibilitySpy.FindManyCalls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DescribeDataset_RelationTerm_PreFiltersTargetsBeforeVisibilityLookup()
    {
        var harness = DenseHarness.Create(hubRelationCount: 120, visibleChildCount: 5, transitiveNeighborCount: 20, goldenQueries: [], withDiscovery: true);
        var visibleInvoice = new TableIdentifier("dense", "dbo", "invoice_visible");
        var hiddenInvoice = new TableIdentifier("dense", "dbo", "invoice_hidden");
        harness.AddRelationTargets(visibleInvoice, isVisible: true, hiddenInvoice, isHiddenVisible: false);
        var executor = harness.CreateExecutor();

        var json = await executor.ExecuteOperationAsync(
            DescribeTool(),
            $$"""{"dataset":"{{DenseHarness.HubIdString}}","relations":"invoice"}""",
            Session());

        using var doc = JsonDocument.Parse(json);
        var relations = doc.RootElement.GetProperty("graphQl").GetProperty("relations");
        relations.GetArrayLength().ShouldBe(1);
        relations[0].GetProperty("dataset").GetString().ShouldBe("dense.dbo.invoice_visible");
        json.ShouldNotContain("invoice_hidden");

        harness.VisibilitySpy.AllBatchCandidates.SetEquals([visibleInvoice, hiddenInvoice]).ShouldBeTrue();
    }

    [Fact]
    public async Task DescribeDataset_ColumnPagingWithNarrowing_DoesNotDoubleApplyOffsetOrCorruptColumnTotal()
    {
        var harness = DenseHarness.Create(hubRelationCount: 5, visibleChildCount: 5, transitiveNeighborCount: 5, goldenQueries: [], extraHubColumns: 10, withDiscovery: true);
        var executor = harness.CreateExecutor();

        var json = await executor.ExecuteOperationAsync(
            DescribeTool(),
            $$"""{"dataset":"{{DenseHarness.HubIdString}}","columnOffset":2,"columnLimit":3,"include":["columns"]}""",
            Session());

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("columnTotal").GetInt32().ShouldBe(12);
        var names = doc.RootElement.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToArray();
        names.ShouldBe(["extra_0", "extra_1", "extra_2"]);
    }

    [Fact]
    public async Task DescribeDataset_LogsExampleResolutionDurationWithoutCatalogNamesOrSids()
    {
        var logger = new RecordingLogger<McpDatasetCatalog>();
        var harness = DenseHarness.Create(hubRelationCount: 10, visibleChildCount: 2, transitiveNeighborCount: 5, goldenQueries: [], logger: logger);

        await harness.Catalog.DescribeDatasetAsync(harness.Principal, DenseHarness.HubIdString, language: null, brief: false);

        logger.Entries.ShouldContain(m => m.Contains("describe_dataset example resolution completed", StringComparison.Ordinal) && m.Contains("elapsedMs=", StringComparison.Ordinal));
        logger.Entries.ShouldContain(m => m.Contains("describe_dataset timing", StringComparison.Ordinal) && m.Contains("examplesMs=", StringComparison.Ordinal));
        logger.Entries.ShouldAllBe(m => !m.Contains("dense.dbo.hub", StringComparison.OrdinalIgnoreCase) && !m.Contains(UserSid, StringComparison.Ordinal));
    }

    private static McpToolDefinition DescribeTool() =>
        McpDatasetTools.Definitions().First(t => string.Equals(t.Name, McpDatasetTools.DescribeDataset, StringComparison.OrdinalIgnoreCase));

    private static McpSessionContext Session() =>
        new("s1", "agent", Tenant, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: UserSid, Roles: ["Analyst"]);

    private sealed class DenseHarness
    {
        public static readonly TableIdentifier HubId = new("dense", "dbo", "hub");
        public const string HubIdString = "dense.dbo.hub";

        public McpDatasetCatalog Catalog { get; }
        public ClaimsPrincipal Principal { get; }
        public CountingMetadataRepository MetadataRepo { get; }
        public CountingConsentRepository ConsentRepo { get; }
        public CountingGoldenQueryRepository GoldenRepo { get; }
        public SpyVisibilityService VisibilitySpy { get; }
        private readonly Dictionary<TableIdentifier, GraphQlTableMapping> _mappings;
        private readonly FakeDiscoveryTableRepository _discoveryRepo;
        private readonly List<TableMetadata> _tables;
        private readonly List<Consent> _consents;
        private readonly StubSnapshotProvider _snapshotProvider;
        private readonly FakeAccessDecisionPoint _pdp;

        private DenseHarness(
            McpDatasetCatalog catalog,
            ClaimsPrincipal principal,
            CountingMetadataRepository metadataRepo,
            CountingConsentRepository consentRepo,
            CountingGoldenQueryRepository goldenRepo,
            SpyVisibilityService visibilitySpy,
            Dictionary<TableIdentifier, GraphQlTableMapping> mappings,
            FakeDiscoveryTableRepository discoveryRepo,
            List<TableMetadata> tables,
            List<Consent> consents,
            StubSnapshotProvider snapshotProvider,
            FakeAccessDecisionPoint pdp)
        {
            Catalog = catalog;
            Principal = principal;
            MetadataRepo = metadataRepo;
            ConsentRepo = consentRepo;
            GoldenRepo = goldenRepo;
            VisibilitySpy = visibilitySpy;
            _mappings = mappings;
            _discoveryRepo = discoveryRepo;
            _tables = tables;
            _consents = consents;
            _snapshotProvider = snapshotProvider;
            _pdp = pdp;
        }

        public static DenseHarness Create(
            int hubRelationCount,
            int visibleChildCount,
            int transitiveNeighborCount,
            IReadOnlyList<GoldenQueryStoreRecord> goldenQueries,
            IReadOnlyCollection<TableIdentifier>? extraVisibleTables = null,
            int extraHubColumns = 0,
            bool withDiscovery = false,
            RecordingLogger<McpDatasetCatalog>? logger = null)
        {
            var hubColumns = new List<TableColumn>
            {
                new() { ColumnName = "id", DataType = "int" },
                new() { ColumnName = "name", DataType = "varchar" }
            };
            for (var i = 0; i < extraHubColumns; i++)
            {
                hubColumns.Add(new TableColumn { ColumnName = $"extra_{i}", DataType = "varchar" });
            }

            var tables = new List<TableMetadata> { MakeTable(HubId, hubColumns, ["id"]) };
            var consents = new List<Consent> { AllowAll(HubId, hubColumns.Select(c => c.ColumnName)) };
            var visibleLookup = new HashSet<TableIdentifier> { HubId };
            if (extraVisibleTables is not null)
            {
                foreach (var extra in extraVisibleTables)
                {
                    visibleLookup.Add(extra);
                }
            }

            var hubRelations = new List<GraphQlRelationMapping>(hubRelationCount);
            var mappings = new Dictionary<TableIdentifier, GraphQlTableMapping>();

            for (var i = 0; i < hubRelationCount; i++)
            {
                var relId = new TableIdentifier("dense", "dbo", $"rel_{i:D3}");
                var relMeta = MakeTable(relId, [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "name", DataType = "varchar" }], ["id"]);
                tables.Add(relMeta);
                if (i < visibleChildCount || visibleLookup.Contains(relId))
                {
                    visibleLookup.Add(relId);
                    consents.Add(AllowAll(relId, ["id", "name"]));
                }

                hubRelations.Add(new GraphQlRelationMapping($"rel_{i:D3}", relId, false, ["id"], ["id"]));

                var childRelations = new List<GraphQlRelationMapping>();
                if (i == 0)
                {
                    for (var j = 0; j < transitiveNeighborCount; j++)
                    {
                        var leafId = new TableIdentifier("dense", "dbo", $"leaf_{j:D3}");
                        var leafMeta = MakeTable(leafId, [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "code", DataType = "varchar" }], ["id"]);
                        tables.Add(leafMeta);
                        if (visibleLookup.Contains(leafId))
                        {
                            consents.Add(AllowAll(leafId, ["id", "code"]));
                        }

                        childRelations.Add(new GraphQlRelationMapping($"leaf_{j:D3}", leafId, false, ["id"], ["id"]));
                        mappings[leafId] = Mapping(leafId, $"dense_dbo_leaf_{j:D3}", ["id", "code"], []);
                    }
                }

                mappings[relId] = Mapping(relId, $"dense_dbo_rel_{i:D3}", ["id", "name"], childRelations);
            }

            mappings[HubId] = Mapping(HubId, "dense_dbo_hub", hubColumns.Select(c => c.ColumnName), hubRelations);

            var snapshotProvider = new StubSnapshotProvider(CatalogSnapshot.Create(new CatalogVersion(1, 1), tables, []));
            var metadataRepo = new CountingMetadataRepository(tables);
            var consentRepo = new CountingConsentRepository(consents);
            var goldenRepo = new CountingGoldenQueryRepository(goldenQueries);
            var versionProvider = new FixedVersionProvider(new CatalogVersion(1, 1));
            var gwOpts = Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options(), Mcp = new McpOptions { Enabled = true } });

            var realVisibility = new CatalogVisibilityService(
                metadataRepo,
                consentRepo,
                gwOpts,
                snapshotProvider: snapshotProvider);
            var visibilitySpy = new SpyVisibilityService(realVisibility);

            var goldenService = new GoldenQueryService(
                gwOpts,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GoldenQueryService>.Instance,
                new NoDemoSwitch(),
                goldenRepo,
                versionProvider);

            var discoveryRepo = new FakeDiscoveryTableRepository();
            discoveryRepo.Tables.AddRange(tables);
            var pdp = new FakeAccessDecisionPoint(req =>
                visibleLookup.Contains(req.Object.Identifier)
                    ? TableAccessDecision.Allowed(
                        req.Object.Identifier,
                        req.Object.Columns.ToDictionary(c => c.ColumnName, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase),
                        hasUnconstrainedColumnAllow: true)
                    : TableAccessDecision.Denied(req.Object.Identifier, "hidden"));

            IDiscoveryService? discoveryService = withDiscovery
                ? new DiscoveryService(
                    discoveryRepo,
                    pdp,
                    consentRepo,
                    new FakeDiscoveryApprovalRepository(),
                    new FakeDiscoveryOwnershipRepository(),
                    new FakeDiscoveryArtifactStore(),
                    new FakeAuditWriter(),
                    gwOpts,
                    Options.Create(new CatalogDiscoveryOptions()),
                    logger: Microsoft.Extensions.Logging.Abstractions.NullLogger<DiscoveryService>.Instance)
                : null;

            var catalog = new McpDatasetCatalog(
                metadataRepo,
                consentRepo,
                new StubGatewayExecutionService(),
                goldenService,
                gwOpts,
                new DictGraphQlMap(mappings),
                logger: logger,
                visibilityService: visibilitySpy,
                discoveryService: discoveryService,
                switches: ReadPathSwitches.From(gwOpts.Value));

            var principal = DiscoveryTestBuilder.User(UserSid, Tenant);
            return new DenseHarness(
                catalog, principal, metadataRepo, consentRepo, goldenRepo, visibilitySpy, mappings, discoveryRepo, tables, consents, snapshotProvider, pdp);
        }

        public void AddRelationTargets(TableIdentifier visibleId, bool isVisible, TableIdentifier hiddenId, bool isHiddenVisible)
        {
            foreach (var (id, vis) in new[] { (visibleId, isVisible), (hiddenId, isHiddenVisible) })
            {
                var meta = MakeTable(id, [new TableColumn { ColumnName = "id", DataType = "int" }], ["id"]);
                _tables.Add(meta);
                _discoveryRepo.Tables.Add(meta);
                if (vis)
                {
                    _consents.Add(AllowAll(id, ["id"]));
                }

                _mappings[id] = Mapping(id, $"dense_dbo_{id.TableName}", ["id"], []);
            }

            var hub = _mappings[HubId];
            var expandedRelations = hub.Relations.Concat([
                new GraphQlRelationMapping(visibleId.TableName, visibleId, false, ["id"], ["id"]),
                new GraphQlRelationMapping(hiddenId.TableName, hiddenId, false, ["id"], ["id"])
            ]).ToList();
            _mappings[HubId] = hub with { Relations = expandedRelations };
            _snapshotProvider.SetSnapshot(CatalogSnapshot.Create(new CatalogVersion(2, 1), _tables, []));
        }

        public GatewayMcpQueryExecutor CreateExecutor() => new(
            new StubRequestExecutorProvider(),
            new StubGatewayExecutionService(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GatewayMcpQueryExecutor>.Instance,
            McpRowCapTests.Options(100),
            new PersistedToolValidator(),
            datasetCatalog: Catalog);

        private static TableMetadata MakeTable(TableIdentifier id, List<TableColumn> cols, IReadOnlyList<string> pk) => new()
        {
            Identifier = id,
            Table = new Table { TableName = id.TableName, SchemaName = id.Schema, IsActive = true },
            PrimaryKeyColumns = pk,
            Columns = cols,
            OwnerTenantId = Tenant
        };

        private static Consent AllowAll(TableIdentifier id, IEnumerable<string> cols) => new()
        {
            Id = Guid.NewGuid(),
            GranteeSid = new Sid(UserSid),
            TenantId = new TenantId(Tenant),
            TableIdentifier = id,
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = cols.Select(c => new ConsentColumnRule { ColumnName = c, AccessLevel = ColumnAccessLevel.Clear }).ToList()
        };

        private static GraphQlTableMapping Mapping(TableIdentifier id, string root, IEnumerable<string> cols, IReadOnlyList<GraphQlRelationMapping> relations) =>
            new(id, root, root, root + "_filter", root + "_order", cols.Select(c => new GraphQlColumnMapping(c, c, "String")).ToList(), relations);
    }

    private sealed class SpyVisibilityService(ICatalogVisibilityService inner) : ICatalogVisibilityService
    {
        public int FindManyCalls { get; private set; }
        public List<int> BatchSizes { get; } = [];
        public HashSet<TableIdentifier> AllBatchCandidates { get; } = [];
        public List<TableIdentifier> ExamplePhaseBatchedTargets { get; } = [];

        public ValueTask<CatalogVisibleSet> GetVisibleAsync(CatalogCaller caller, Autheris.Application.Governance.Contracts.SchemaContractDefinition? contract = null, CancellationToken ct = default) =>
            inner.GetVisibleAsync(caller, contract, ct);

        public ValueTask<CatalogVisibleRow?> FindVisibleAsync(CatalogCaller caller, TableIdentifier table, CancellationToken ct = default) =>
            inner.FindVisibleAsync(caller, table, ct);

        public ValueTask<IReadOnlySet<TableIdentifier>> FilterVisibleAsync(CatalogCaller caller, IReadOnlyCollection<TableIdentifier> candidates, CancellationToken ct = default) =>
            inner.FilterVisibleAsync(caller, candidates, ct);

        public ValueTask<CatalogVisibleBatch> FindVisibleManyAsync(CatalogCaller caller, IReadOnlyCollection<TableIdentifier> candidates, CancellationToken ct = default)
        {
            FindManyCalls++;
            BatchSizes.Add(candidates.Count);
            foreach (var c in candidates)
            {
                AllBatchCandidates.Add(c);
                if (candidates.Count < 10)
                {
                    ExamplePhaseBatchedTargets.Add(c);
                }
            }

            return inner.FindVisibleManyAsync(caller, candidates, ct);
        }
    }

    private sealed class CountingMetadataRepository(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        private int _singleReads;
        public int SingleReadCount => Volatile.Read(ref _singleReads);

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _singleReads);
            return Task.FromResult(tables.FirstOrDefault(t => t.Identifier == table));
        }

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult(tables);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class CountingConsentRepository(IReadOnlyList<Consent> consents) : IConsentRepository
    {
        private int _allActiveCalls;
        public int AllActiveCalls => Volatile.Read(ref _allActiveCalls);

        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>([]);

        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _allActiveCalls);
            return Task.FromResult(consents);
        }

        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CountingGoldenQueryRepository(IReadOnlyList<GoldenQueryStoreRecord> rows) : IGoldenQueryRepository
    {
        private int _calls;
        public int GetForTenantCalls => Volatile.Read(ref _calls);

        public Task ReplaceForTableAsync(TenantId tenant, string tableId, GoldenQuerySource source, IReadOnlyList<GoldenQueryStoreEntry> entries, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<GoldenQueryStoreRecord>> GetForTenantAsync(TenantId tenant, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(rows);
        }
    }

    private sealed class DictGraphQlMap(IReadOnlyDictionary<TableIdentifier, GraphQlTableMapping> map) : IGraphQlCatalogMap
    {
        public Task<GraphQlTableMapping?> GetTableAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(map.TryGetValue(table, out var entry) ? entry : null);
        public Task<string?> GetRootFieldAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(map.TryGetValue(table, out var entry) ? entry.QueryField : null);
        public Task<TableIdentifier?> ResolveRootFieldAsync(string field, CancellationToken ct = default) =>
            Task.FromResult<TableIdentifier?>(null);
        public Task<IReadOnlyList<TableIdentifier>?> ResolveDocumentTablesAsync(string document, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableIdentifier>?>(null);
    }

    private sealed class StubSnapshotProvider(CatalogSnapshot initial) : ICatalogSnapshotProvider
    {
        private CatalogSnapshot _snapshot = initial;
        public bool IsInitialized => true;
        public CatalogSnapshot CurrentSnapshot => _snapshot;
        public void SetSnapshot(CatalogSnapshot snapshot) => _snapshot = snapshot;
        public ValueTask<CatalogSnapshot> GetSnapshotAsync(CancellationToken ct = default) => ValueTask.FromResult(_snapshot);
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedVersionProvider(CatalogVersion version) : ICatalogVersionProvider
    {
        public ValueTask<CatalogVersion> GetCatalogVersionAsync(CancellationToken ct = default) => ValueTask.FromResult(version);
        public Task<string> GetVersionAsync(CancellationToken ct = default) => Task.FromResult(version.Formatted);
    }

    private sealed class NoDemoSwitch : IDemoDataSwitch
    {
        public bool Enabled => false;
    }

    private sealed class StubRequestExecutorProvider : IRequestExecutorProvider
    {
        public System.Collections.Immutable.ImmutableArray<string> SchemaNames => [];
        public ValueTask<IRequestExecutor> GetExecutorAsync(string? schemaName = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(formatter(state, exception));
    }
}
