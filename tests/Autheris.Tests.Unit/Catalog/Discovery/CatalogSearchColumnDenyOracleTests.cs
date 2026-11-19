namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Tests.Unit.Governance.Classification;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Catalog search must match only the metadata the caller may see for a dataset (the same columns and texts describe_dataset returns):
/// a hit through a column that is denied to the caller, or through any column of a dataset the caller has no access to, is an oracle.
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogSearchColumnDenyOracleTests
{
    private const string Marker = "zebracornmarker";
    private const string DeniedColumn = "diagnosis_code";
    private const string VisibleOnlyTerm = "pineapplezone";
    private const string MaskedMarker = "hiddenmaskmarker";
    private const string RandomTerm = "quuxbazrandomword";

    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly Sid UserSid = new("S-1-5-21-USER-813");
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private enum Caller { DenyOneColumn, ClearAll, NoConsent, MaskedHiddenText }

    private static TableMetadata BuildTable(string name, string markerColumn = DeniedColumn, bool hiddenMarker = true) => new()
    {
        Identifier = new TableIdentifier("crm", "dbo", name),
        OwnerTenantId = Tenant.Value,
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "crm",
            SchemaName = "dbo",
            TableName = name,
            DisplayName = name,
            Description = "Directory of records",
            IsActive = true
        },
        Columns =
        [
            Col("id", "Surrogate row key."),
            Col("fullname", "Display label of the person."),
            Col("email", "Contact mailbox."),
            Col("birth_date", "Calendar day of birth."),
            Col("region", $"Sales area {VisibleOnlyTerm} of the record."),
            Col("status", "Lifecycle state."),
            Col("notes", $"Free form remarks {MaskedMarker}."),
            Col(markerColumn, hiddenMarker ? $"Internal classification {Marker} assigned by the clinic." : "Internal classification assigned by the clinic.")
        ]
    };

    private static TableColumn Col(string name, string description) => new()
    {
        Id = Guid.NewGuid(),
        ColumnName = name,
        DataType = "varchar",
        DisplayName = name,
        Description = description
    };

    private static FakeAccessDecisionPoint Pdp(Caller caller, string? deniedTable) => new(req =>
    {
        if (caller == Caller.NoConsent)
        {
            return TableAccessDecision.Denied(req.Object.Identifier, "no consent");
        }

        var levels = req.Object.Columns.ToDictionary(
            c => c.ColumnName,
            c => c.ColumnName switch
            {
                DeniedColumn when caller == Caller.DenyOneColumn && (deniedTable is null || req.Object.Identifier.TableName == deniedTable || req.Object.Identifier.TableName.StartsWith("bulk", StringComparison.Ordinal)) => ColumnAccessLevel.Deny,
                "email" => ColumnAccessLevel.Mask,
                "notes" when caller == Caller.MaskedHiddenText => ColumnAccessLevel.Mask,
                _ => ColumnAccessLevel.Clear
            },
            StringComparer.OrdinalIgnoreCase);
        var decision = TableAccessDecision.Allowed(req.Object.Identifier, levels);
        return caller == Caller.MaskedHiddenText
            ? decision with { HiddenCatalogTextColumns = new HashSet<string>(["notes"], StringComparer.OrdinalIgnoreCase) }
            : decision;
    });

    private static async Task<(DiscoveryService Discovery, FakeDiscoveryTableRepository Tables)> CreateAsync(
        Caller caller,
        params TableMetadata[] tables) => await CreateAsync(caller, denyOnEveryTable: false, tables);

    private static async Task<(DiscoveryService Discovery, FakeDiscoveryTableRepository Tables)> CreateAsync(
        Caller caller,
        bool denyOnEveryTable,
        params TableMetadata[] tables)
    {
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.AddRange(tables);
        var engine = new CatalogSearchEngine(
            new ConstantVectorGenerator(),
            Options.Create(new CatalogSearchOptions()),
            NullLogger<CatalogSearchEngine>.Instance,
            new AdjustableClassificationTimeProvider(FixedNow));
        await engine.RebuildIndexAsync(tables);

        var discovery = new DiscoveryService(
            repo,
            Pdp(caller, denyOnEveryTable ? null : tables[0].Identifier.TableName),
            new FakeDiscoveryConsentRepository(),
            new FakeDiscoveryApprovalRepository(),
            new FakeDiscoveryOwnershipRepository(),
            new FakeDiscoveryArtifactStore(),
            new FakeAuditWriter(),
            Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }),
            Options.Create(new CatalogDiscoveryOptions()),
            searchEngine: engine);
        return (discovery, repo);
    }

    private static ClaimsPrincipal Principal() => DiscoveryTestBuilder.User(UserSid.Value, Tenant.Value);

    private static CatalogCaller CallerOf() => DiscoveryTestBuilder.Caller(Principal(), Tenant);

    private static async Task<IReadOnlyList<DiscoveryObject>> SearchAsync(DiscoveryService discovery, string term, string? mode, int limit = 25) =>
        await discovery.SearchAsync(term, new DiscoverySearchOptions(null, limit, mode, null, DiscoverySurface.Search), CallerOf());

    private static McpDatasetCatalog McpOf(DiscoveryService discovery, FakeDiscoveryTableRepository tables) => new(
        tables,
        new TestInMemoryConsentRepository([], filterWindow: false),
        new FakeMcpGatewayExecution(),
        new FakeMcpAuditRepo(),
        options: Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options(), Mcp = new McpOptions { Enabled = true } }),
        discoveryService: discovery);

    [Theory]
    [InlineData(DeniedColumn)]
    [InlineData(Marker)]
    [InlineData(RandomTerm)]
    public async Task ListDatasets_Search_DeniedColumnNameOrDescription_NoHit(string term)
    {
        var (discovery, tables) = await CreateAsync(Caller.DenyOneColumn, BuildTable("patients"));

        var list = await McpOf(discovery, tables).ListDatasetsAsync(Principal(), term, null, CancellationToken.None);

        list.Datasets.ShouldBeEmpty();
        list.Total.ShouldBe(0);
    }

    [Fact]
    public async Task ListDatasets_Search_TermOnlyInVisibleColumn_StillHits()
    {
        var (discovery, tables) = await CreateAsync(Caller.DenyOneColumn, BuildTable("patients"));

        var list = await McpOf(discovery, tables).ListDatasetsAsync(Principal(), VisibleOnlyTerm, null, CancellationToken.None);

        list.Datasets.Count.ShouldBe(1);
        list.Total.ShouldBe(1);
    }

    [Fact]
    public async Task Search_TermMatchingOnlyDeniedColumnDescription_ReturnsNoHit()
    {
        var (discovery, _) = await CreateAsync(Caller.DenyOneColumn, BuildTable("patients"));

        (await SearchAsync(discovery, Marker, "keyword")).ShouldBeEmpty();
        (await SearchAsync(discovery, Marker, null)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_HitOnlyInDeniedColumnStatistics_ReturnsNoHit()
    {
        // Statistics of a denied column are never part of the searchable text: the denied name finds nothing for the denied caller.
        var (discovery, _) = await CreateAsync(Caller.DenyOneColumn, BuildTable("patients"));

        (await SearchAsync(discovery, DeniedColumn, "keyword")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("keyword", DeniedColumn)]
    [InlineData("keyword", Marker)]
    [InlineData("keyword", RandomTerm)]
    [InlineData("hybrid", DeniedColumn)]
    [InlineData("hybrid", Marker)]
    [InlineData("hybrid", RandomTerm)]
    [InlineData("semantic", DeniedColumn)]
    [InlineData("semantic", Marker)]
    [InlineData("semantic", RandomTerm)]
    [InlineData("all", DeniedColumn)]
    [InlineData("all", Marker)]
    [InlineData("all", RandomTerm)]
    [InlineData(null, Marker)]
    public async Task Search_AnyMode_DeniedColumnNameOrDescription_NoHit(string? mode, string term)
    {
        var (discovery, _) = await CreateAsync(Caller.DenyOneColumn, BuildTable("patients"));

        (await SearchAsync(discovery, term, mode)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("keyword")]
    [InlineData("hybrid")]
    public async Task Search_TermOnlyInVisibleColumn_StillHits(string mode)
    {
        var (discovery, _) = await CreateAsync(Caller.DenyOneColumn, BuildTable("patients"));

        var hits = await SearchAsync(discovery, VisibleOnlyTerm, mode);

        hits.Count.ShouldBe(1);
        hits[0].Columns.ShouldNotContain(c => c.Name == DeniedColumn && c.State != DiscoveryColumnState.NoAccess);
    }

    [Theory]
    [InlineData("keyword")]
    [InlineData("hybrid")]
    public async Task Search_CallerWithClearOnColumn_MatchesMarker(string mode)
    {
        var (discovery, _) = await CreateAsync(Caller.ClearAll, BuildTable("patients"));

        (await SearchAsync(discovery, Marker, mode)).Count.ShouldBe(1);
        (await SearchAsync(discovery, DeniedColumn, mode)).Count.ShouldBe(1);
        (await SearchAsync(discovery, RandomTerm, mode)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAsync_Search_DeniedColumn_NoHitAndTotalZero_ClearCallerHits()
    {
        var denied = await CreateAsync(Caller.DenyOneColumn, BuildTable("patients"));
        var clear = await CreateAsync(Caller.ClearAll, BuildTable("patients"));

        foreach (var term in new[] { DeniedColumn, Marker, RandomTerm })
        {
            var page = await denied.Discovery.ListAsync(new DiscoveryQuery(Search: term), CallerOf());
            page.Items.ShouldBeEmpty();
            page.TotalCount.ShouldBe(0);
        }

        (await denied.Discovery.ListAsync(new DiscoveryQuery(Search: VisibleOnlyTerm), CallerOf())).TotalCount.ShouldBe(1);
        (await clear.Discovery.ListAsync(new DiscoveryQuery(Search: Marker), CallerOf())).TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task ListAsync_Search_TotalAndPagingCountOnlyVisibleHits()
    {
        // "patients" (first in catalog order) hides the marker column from the caller, "visits" shows it.
        var (discovery, _) = await CreateAsync(Caller.DenyOneColumn, BuildTable("patients"), BuildTable("visits"));

        var page = await discovery.ListAsync(new DiscoveryQuery(Search: Marker, First: 1, After: 0), CallerOf());

        page.TotalCount.ShouldBe(1);
        page.HasNextPage.ShouldBeFalse();
        page.Items.Select(i => i.Name).ShouldBe(["visits"]);
    }

    [Theory]
    [InlineData("keyword")]
    [InlineData("hybrid")]
    public async Task Search_LimitAppliesAfterVerification(string mode)
    {
        var (discovery, _) = await CreateAsync(Caller.DenyOneColumn, BuildTable("patients"), BuildTable("visits"));

        var hits = await SearchAsync(discovery, Marker, mode, limit: 1);

        hits.Select(h => h.Name).ShouldBe(["visits"]);
    }

    [Fact]
    public async Task SearchCatalogDetailed_DeniedColumn_NoHitForAllTermsAndModes()
    {
        var (discovery, tables) = await CreateAsync(Caller.DenyOneColumn, BuildTable("patients"));
        var options = new GatewayOptions { Tenancy = TenancyShape.Multi.Options() };
        var consents = new TestInMemoryConsentRepository([], filterWindow: false);
        var service = new CatalogDiscoveryService(
            tables,
            new FakeAuditWriter(),
            Options.Create(options),
            Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(tables, consents, options),
            consentRepository: consents,
            discoveryService: discovery);
        var context = new RequestContext(Tenant, UserSid, User: Principal());

        foreach (var mode in new[] { CatalogSearchMode.KeywordBm25, CatalogSearchMode.Hybrid, CatalogSearchMode.SemanticVector })
        {
            foreach (var term in new[] { DeniedColumn, Marker, RandomTerm })
            {
                (await service.SearchCatalogDetailedAsync(new CatalogSearchQuery(term, Mode: mode), context)).ShouldBeEmpty();
            }
        }

        (await service.SearchCatalogAsync(Marker, null, context)).ShouldBeEmpty();
        (await service.SearchCatalogDetailedAsync(new CatalogSearchQuery(VisibleOnlyTerm, Mode: CatalogSearchMode.KeywordBm25), context)).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(Marker)]
    [InlineData(VisibleOnlyTerm)]
    [InlineData(DeniedColumn)]
    [InlineData("directory")]
    public async Task NoConsentCaller_SearchMatchesOnlyWhatDescribeDatasetShows(string term)
    {
        var (discovery, tables) = await CreateAsync(Caller.NoConsent, BuildTable("patients"));
        var mcp = McpOf(discovery, tables);

        var description = await mcp.DescribeDatasetAsync(Principal(), "crm.dbo.patients", language: null, brief: false);
        var shown = string.Join(' ', new[] { description.Description, description.DisplayName }
            .Concat(description.Columns.SelectMany(c => new[] { c.Name, c.DisplayName, c.Description })));
        var describeShowsTerm = shown.Contains(term, StringComparison.OrdinalIgnoreCase);

        var list = await mcp.ListDatasetsAsync(Principal(), term, null, CancellationToken.None);
        var keyword = await SearchAsync(discovery, term, "keyword");
        var hybrid = await SearchAsync(discovery, term, "hybrid");

        (list.Datasets.Count > 0).ShouldBe(describeShowsTerm);
        list.Total.ShouldBe(describeShowsTerm ? 1 : 0);
        (keyword.Count > 0).ShouldBe(describeShowsTerm);
        (hybrid.Count > 0).ShouldBe(describeShowsTerm);
    }

    [Fact]
    public async Task NoConsentCaller_TableLevelText_StillMatches()
    {
        var (discovery, _) = await CreateAsync(Caller.NoConsent, BuildTable("patients"));

        (await SearchAsync(discovery, "directory", "keyword")).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Search_OrderOfColumnRestrictedHits_DoesNotDependOnDeniedColumnText()
    {
        // Both tables are column-restricted for the caller and match only through the visible term; "zeta" additionally holds the
        // marker in its denied column, which would rank it first if hidden text moved the order.
        var withHidden = await CreateAsync(Caller.DenyOneColumn, denyOnEveryTable: true, BuildTable("zeta"), BuildTable("alpha", hiddenMarker: false));
        var withoutHidden = await CreateAsync(Caller.DenyOneColumn, denyOnEveryTable: true, BuildTable("zeta", hiddenMarker: false), BuildTable("alpha", hiddenMarker: false));
        var query = $"{VisibleOnlyTerm} {Marker}";

        var orderWith = (await SearchAsync(withHidden.Discovery, query, "keyword")).Select(h => h.Name).ToList();
        var orderWithout = (await SearchAsync(withoutHidden.Discovery, query, "keyword")).Select(h => h.Name).ToList();

        orderWith.ShouldBe(["alpha", "zeta"]);
        orderWith.ShouldBe(orderWithout);
    }

    [Fact]
    public async Task CatalogServiceWithoutDecisionPoint_DeniedColumn_NoHit()
    {
        var table = BuildTable("patients") with
        {
            ColumnMaskingRules = new Dictionary<string, MaskingRule> { [DeniedColumn] = new MaskingRule { RuleType = "DENY" } }
        };
        var tables = new FakeDiscoveryTableRepository();
        tables.Tables.Add(table);
        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            TableIdentifier = table.Identifier,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            Effect = ConsentEffect.Allow,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(30)
        };
        var consents = new TestInMemoryConsentRepository([consent], filterWindow: false);
        var options = new GatewayOptions { Tenancy = TenancyShape.Multi.Options() };
        var engine = new CatalogSearchEngine(
            new ConstantVectorGenerator(),
            Options.Create(new CatalogSearchOptions()),
            NullLogger<CatalogSearchEngine>.Instance,
            new AdjustableClassificationTimeProvider(FixedNow));
        await engine.RebuildIndexAsync([table]);
        var service = new CatalogDiscoveryService(
            tables,
            new FakeAuditWriter(),
            Options.Create(options),
            Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(tables, consents, options),
            searchEngine: engine,
            consentRepository: consents,
            timeProvider: new AdjustableClassificationTimeProvider(FixedNow));
        var context = new RequestContext(Tenant, UserSid, User: Principal());

        foreach (var term in new[] { DeniedColumn, Marker, RandomTerm })
        {
            (await service.SearchCatalogDetailedAsync(new CatalogSearchQuery(term, Mode: CatalogSearchMode.KeywordBm25), context)).ShouldBeEmpty();
            (await service.SearchCatalogAsync(term, null, context)).ShouldBeEmpty();
        }

        (await service.SearchCatalogDetailedAsync(new CatalogSearchQuery("directory", Mode: CatalogSearchMode.KeywordBm25), context)).Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("keyword")]
    [InlineData("hybrid")]
    public async Task MaskedColumnWithHiddenCatalogText_TermOnlyInItsDescription_NoHit_ClearCallerHits(string mode)
    {
        var masked = await CreateAsync(Caller.MaskedHiddenText, BuildTable("patients"));
        var clear = await CreateAsync(Caller.ClearAll, BuildTable("patients"));

        (await SearchAsync(masked.Discovery, MaskedMarker, mode)).ShouldBeEmpty();
        (await SearchAsync(clear.Discovery, MaskedMarker, mode)).Count.ShouldBe(1);
        // The masked column itself stays listed by describe_dataset, so its name remains searchable.
        (await SearchAsync(masked.Discovery, "notes", mode)).Count.ShouldBe(1);

        var list = await McpOf(masked.Discovery, masked.Tables).ListDatasetsAsync(Principal(), MaskedMarker, null, CancellationToken.None);
        list.Datasets.ShouldBeEmpty();
        list.Total.ShouldBe(0);
        (await McpOf(clear.Discovery, clear.Tables).ListDatasetsAsync(Principal(), MaskedMarker, null, CancellationToken.None)).Total.ShouldBe(1);
    }

    [Fact]
    public async Task Search_VerifiedHitBeyondTheFirstIndexWindow_IsStillFound()
    {
        // 80 column-restricted tables outrank the one visible table in the index (the marker repeats in their denied column), so the
        // first window holds no verifiable hit.
        var bulk = Enumerable.Range(0, 80)
            .Select(i =>
            {
                var t = BuildTable($"bulk{i:D2}");
                return t with { Columns = [.. t.Columns.Take(7), Col(DeniedColumn, string.Join(' ', Enumerable.Repeat(Marker, 6)))] };
            });
        var (discovery, _) = await CreateAsync(Caller.DenyOneColumn, [.. bulk, BuildTable("visits")]);

        var hits = await SearchAsync(discovery, Marker, "keyword", limit: 1);

        hits.Select(h => h.Name).ShouldBe(["visits"]);
    }

    [Fact]
    public async Task CatalogServiceWithoutConsentRepository_FailsClosed_OnlyTableLevelTextMatches()
    {
        var table = BuildTable("patients");
        var tables = new FakeDiscoveryTableRepository();
        tables.Tables.Add(table);
        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            TableIdentifier = table.Identifier,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            Effect = ConsentEffect.Allow,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(30)
        };
        var consents = new TestInMemoryConsentRepository([consent], filterWindow: false);
        var options = new GatewayOptions { Tenancy = TenancyShape.Multi.Options() };
        var engine = new CatalogSearchEngine(
            new ConstantVectorGenerator(),
            Options.Create(new CatalogSearchOptions()),
            NullLogger<CatalogSearchEngine>.Instance,
            new AdjustableClassificationTimeProvider(FixedNow));
        await engine.RebuildIndexAsync([table]);
        var service = new CatalogDiscoveryService(
            tables,
            new FakeAuditWriter(),
            Options.Create(options),
            Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(tables, consents, options),
            searchEngine: engine);
        var context = new RequestContext(Tenant, UserSid, User: Principal());

        (await service.SearchCatalogDetailedAsync(new CatalogSearchQuery("region", Mode: CatalogSearchMode.KeywordBm25), context)).ShouldBeEmpty();
        (await service.SearchCatalogDetailedAsync(new CatalogSearchQuery("directory", Mode: CatalogSearchMode.KeywordBm25), context)).Count.ShouldBe(1);
    }

    private sealed class ConstantVectorGenerator : ILocalEmbeddingGenerator
    {
        public int EmbeddingDimensions => 4;
        public string ModelIdentifier => "constant-test";
        public ReadOnlyMemory<float> GenerateNormalizedEmbedding(string text) => new float[] { 1, 0, 0, 0 };

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> GenerateNormalizedEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(texts.Select(_ => (ReadOnlyMemory<float>)new float[] { 1, 0, 0, 0 }).ToList());
    }

    private sealed class FakeMcpAuditRepo : IAuditLogRepository
    {
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction existingTx, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FakeMcpGatewayExecution : IGatewayExecutionService
    {
        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
            ClaimsPrincipal? principal, TableIdentifier table, int? first = null, int? after = null, CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>(([], TableAccessDecision.Denied(table, "fake")));

        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(
            ClaimsPrincipal? principal, TableIdentifier table, int? first, int? after, IReadOnlyDictionary<string, object?>? queryArguments, IReadOnlyList<string>? requestedFields = null, IReadOnlyDictionary<string, string[]>? requestHeaders = null, CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<IReadOnlyDictionary<string, object?>>, TableAccessDecision)>(([], TableAccessDecision.Denied(table, "fake")));

        public Task<TableAccessDecision> CheckTableAccessAsync(ClaimsPrincipal? principal, TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(TableAccessDecision.Denied(table, "fake"));
    }
}
