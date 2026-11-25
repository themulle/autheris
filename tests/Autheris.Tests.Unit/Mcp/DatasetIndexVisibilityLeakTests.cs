#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.OData.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Catalog;
using Autheris.Tests.Unit.TestDoubles;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #404 (leak fix) as relaxed by #1105: a key or an index over a denied, discovery-hidden or unresolved column is omitted whole from
/// describe_dataset, the REST catalog detail and the OpenAPI document; a key or index over a masked column is kept (the caller sees the column
/// name in the same answer, and filtering, ordering and joining on it stay refused). No surface emits an index name.
/// </summary>
[Trait("Category", "Security")]
public sealed class DatasetIndexVisibilityLeakTests
{
    private const string Tenant = "tenant-a";
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");

    private static TableMetadata Table() => new()
    { ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor("email"),
        Identifier = Orders,
        Table = new Table { TableName = "orders", SchemaName = "public", IsActive = true },
        PrimaryKeyColumns = ["id", "email"],
        Indexes =
        [
            new TableIndexDefinition("ix_leaks_email_name", ["email"], false),
            new TableIndexDefinition("ux_secret_id_email", ["id", "email"], true),
            new TableIndexDefinition("ix_visible", ["id", "amount"], false)
        ],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "amount", DataType = "decimal" },
            new TableColumn { ColumnName = "email", DataType = "varchar" }
        ]
    };

    private static readonly IOpenApiIngestionService UnusedIngestion = Substitute.For<IOpenApiIngestionService>();
    private static readonly ICatalogSearchEngine UnusedSearch = Substitute.For<ICatalogSearchEngine>();

    private static McpDatasetCatalog Mcp(TableMetadata table, IConsentResolutionService? resolution = null, ColumnAccessLevel emailLevel = ColumnAccessLevel.Mask)
    {
        var metadata = new InMemoryTableMetadataRepository(table);
        var consents = new InMemoryConsentRepository(
            new Consent
            {
                TableIdentifier = Orders,
                TenantId = new TenantId(Tenant),
                Effect = ConsentEffect.Allow,
                GranteeSid = new Sid("S-1-USER"),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(1),
                ColumnRules =
                [
                    new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "amount", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "email", AccessLevel = emailLevel }
                ]
            });
        return new McpDatasetCatalog(metadata, consents, new StubGatewayExecutionService(), StubGoldenQueryService.Instance,
            Options.Create(new GatewayOptions()), StubGraphQlCatalogMap.Instance,
            consentResolution: resolution,
            visibilityService: CatalogVisibilityTestFactory.Create(metadata, consents));
    }

    private static ClaimsPrincipal User() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", Tenant)], "MCP"));

    private static CatalogDiscoveryService Rest(TableMetadata table)
    {
        var repo = new InMemoryTableMetadataRepository(table);
        var consents = new InMemoryConsentRepository();
        var options = Wp0.Options(TenancyShape.Single);
        return new CatalogDiscoveryService(
            repo,
            AuditWriterTestServices.Over(new InMemoryAuditLogRepository()),
            Options.Create(options),
            CatalogVisibilityTestFactory.Create(repo, consents, options),
            new InMemoryKeyVaultSecretProvider(),
            UnusedIngestion,
            UnusedSearch,
            NullLogger<CatalogDiscoveryService>.Instance);
    }

    private static RequestContext Admin() => new(new TenantId("t"), new Sid("user:x"), User: Wp0.User("user:x", null, ["GovernanceAdmin"]));

    private static string Columns(IReadOnlyList<DatasetIndexInfo>? indexes) =>
        string.Join("|", (indexes ?? []).Select(i => string.Join(",", i.Columns)));

    [Fact]
    public async Task I1105_T_MaskedColumn_KeysAndIndexesKept_Mcp_Rest_GraphQl()
    {
        var mcp = await Mcp(Table()).DescribeDatasetAsync(User(), "sales.public.orders");
        var rest = await Rest(Table()).GetDatasetDetailAsync(Orders, Admin());

        // GraphQL: the MCP GraphQL tool answers through the same DescribeDatasetAsync path (GatewayMcpQueryExecutor), so the
        // describe assertions below cover it; there is no separate GraphQL projection.
        // Parity (I404-SC-L5): both surfaces answer with the same whole indexes, unique first; "email" is masked, not denied.
        mcp.Columns.Single(c => c.Name == "email").Access.ShouldBe("mask");
        Columns(mcp.Indexes).ShouldBe("id,email|email|id,amount");
        Columns(rest!.Indexes).ShouldBe("id,email|email|id,amount");
        mcp.Columns.Where(c => c.PrimaryKey).Select(c => c.Name).ShouldBe(new[] { "id", "email" });
    }

    [Fact]
    public async Task I404_T_SL2_DeniedOrMissingAccessLevel_WholeIndexOmitted_FailClosed()
    {
        // A resolver that returns nothing / fails: every column is Deny, so no key and no index may appear.
        var mcp = await Mcp(Table(), new ThrowingResolution()).DescribeDatasetAsync(User(), "sales.public.orders");

        (mcp.Indexes ?? []).ShouldBeEmpty();
        mcp.Columns.ShouldAllBe(c => !c.PrimaryKey);

        var plain = Table();
        DatasetIndexProjection.Project(plain, null).Indexes.ShouldBeEmpty();
        DatasetIndexProjection.Project(plain, null).PrimaryKeyColumns.ShouldBeEmpty();
        DatasetIndexProjection.Project(plain, new HashSet<string>()).Indexes.ShouldBeEmpty();
        // A column that does not resolve in the table is never clear, even if named in the set.
        var stale = plain with { Indexes = [new TableIndexDefinition("ix", ["id", "ghost"], false)] };
        DatasetIndexProjection.Project(stale, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "ghost" }).Indexes.ShouldBeEmpty();
        DatasetIndexProjection.ClearSet(null).ShouldBeEmpty();
        DatasetIndexProjection.ClearSet(new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Mask }).ShouldBeEmpty();
    }

    [Fact]
    public async Task I404_T_SL3_DescribeDataset_And_RestDetail_EmitNoIndexNames()
    {
        var mcp = await Mcp(Table()).DescribeDatasetAsync(User(), "sales.public.orders");
        var rest = await Rest(Table()).GetDatasetDetailAsync(Orders, Admin());

        foreach (var json in new[] { JsonSerializer.Serialize(mcp), JsonSerializer.Serialize(rest) })
        {
            json.ShouldNotContain("ix_");
            json.ShouldNotContain("ux_");
            json.ShouldNotContain("\"Name\":\"ix");
        }

        typeof(DatasetIndexInfo).GetProperties().Select(p => p.Name).Order().ShouldBe(new[] { "Columns", "Directions", "Unique" });

        // OpenAPI has no caller context: it emits no index and no index name at all.
        var repo = new InMemoryTableMetadataRepository(Table());
        var openApi = await new DynamicOpenApiGenerator(repo, NullLogger<DynamicOpenApiGenerator>.Instance).GenerateOpenApiJsonAsync(null);
        openApi.ShouldNotContain("x-autheris-indexes");
        openApi.ShouldNotContain("ix_leaks_email_name");
        openApi.ShouldNotContain("ux_secret_id_email");
        openApi.ShouldNotContain("ix_visible");
    }

    [Fact]
    public async Task I1105_T_PrimaryKeyOrder_KeptWhenPkColumnMasked_OmittedWhenNotVisible_AllSurfaces()
    {
        var mcp = await Mcp(Table()).DescribeDatasetAsync(User(), "sales.public.orders");
        var rest = await Rest(Table()).GetDatasetDetailAsync(Orders, Admin());

        mcp.PrimaryKeyOrder!.Select(k => k.Column).ShouldBe(new[] { "id", "email" });
        rest!.PrimaryKeys.ShouldBe(new[] { "id", "email" });
        rest.Columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ShouldBe(new[] { "id", "email" });
        // The visible set (Clear or Mask) decides: a key column outside it omits the whole key.
        DatasetIndexProjection.Project(Table(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id" }).PrimaryKeyColumns.ShouldBeEmpty();
        DatasetIndexProjection.Project(Table(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "id", "email" })
            .PrimaryKeyColumns.ShouldBe(new[] { "id", "email" });
    }

    [Fact]
    public void I404_T_SL5_AllClear_EmitsColumnsDirectionsUnique_Capped32x32()
    {
        var names = Enumerable.Range(0, 40).Select(i => $"c{i}").ToArray();
        var table = new TableMetadata
        {
            Identifier = Orders,
            Table = new Table { TableName = "orders", SchemaName = "public" },
            Columns = names.Select(n => new TableColumn { ColumnName = n, DataType = "int" }).ToList(),
            PrimaryKeyColumns = ["c0", "c1"],
            Indexes = names.Select(n => new TableIndexDefinition("ix_" + n, [n], n == "c0"))
                .Append(new TableIndexDefinition("ix_wide", names, false))
                .Append(new TableIndexDefinition("ix_dup", ["c1"], false))
                .ToList()
        };
        var clear = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

        var projected = DatasetIndexProjection.Project(table, clear);

        projected.PrimaryKeyColumns.ShouldBe(new[] { "c0", "c1" });
        projected.Indexes.Count.ShouldBe(32);
        projected.Indexes[0].ShouldBe(new DatasetIndexInfo(["c0"], true), DatasetIndexInfoComparer.Instance);
        projected.Indexes.ShouldAllBe(i => i.Columns.Count <= 32);

        // Cap after the filter: an invisible index does not consume a slot and the visible count stays independent of it.
        var withHidden = table with { Indexes = [new TableIndexDefinition("h", ["c39"], false), .. table.Indexes.Take(32)] };
        var partial = DatasetIndexProjection.Project(withHidden, new HashSet<string>(names.Take(32), StringComparer.OrdinalIgnoreCase));
        partial.Indexes.Count.ShouldBe(32);
        partial.Indexes.ShouldAllBe(i => !i.Columns.Contains("c39"));
    }

    private static readonly TableIdentifier Plain = new("sales", "dbo", "orders");

    private static TableMetadata PlainTable() => Wp0.Table("sales", "orders", "Orders", ["id", "amount"]) with
    {
        PrimaryKeyColumns = ["id", "amount"],
        Indexes = [new TableIndexDefinition("ix_amount", ["amount"], false), new TableIndexDefinition("ix_id", ["id"], true)]
    };

    private static async Task<DatasetIndexInfoView> RestAsUserAsync(ColumnAccessLevel amountLevel, IConsentResolutionService? resolution = null)
    {
        var consent = new Consent
        {
            TableIdentifier = Plain,
            TenantId = Wp0.TenantA,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-USER"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "amount", AccessLevel = amountLevel }
            ]
        };
        var tables = Wp0.Tables(PlainTable());
        var consents = Wp0.Consents(consent);
        var options = Wp0.Options(TenancyShape.Multi);
        var service = new CatalogDiscoveryService(
            tables,
            AuditWriterTestServices.Over(new InMemoryAuditLogRepository()),
            Options.Create(options),
            CatalogVisibilityTestFactory.Create(tables, consents, options),
            new InMemoryKeyVaultSecretProvider(),
            UnusedIngestion,
            UnusedSearch,
            NullLogger<CatalogDiscoveryService>.Instance,
            consentResolution: resolution,
            consentRepository: consents);

        var detail = await service.GetDatasetDetailAsync(Plain, Wp0.Context(Wp0.User("S-USER", "tenant-a")));
        detail.ShouldNotBeNull();
        return new DatasetIndexInfoView(Columns(detail.Indexes), detail.PrimaryKeys.Count, detail.Columns.Any(c => c.IsPrimaryKey));
    }

    private sealed record DatasetIndexInfoView(string Indexes, int PrimaryKeys, bool AnyPkFlag);

    [Fact]
    public async Task I404_T_SL1_Rest_NonAdmin_AllClear_ShowsKeysAndIndexes()
    {
        var view = await RestAsUserAsync(ColumnAccessLevel.Clear);

        view.Indexes.ShouldBe("id|amount");
        view.PrimaryKeys.ShouldBe(2);
    }

    [Theory]
    [InlineData(ColumnAccessLevel.Deny)]
    public async Task I404_T_SL1_Rest_NonAdmin_ConsentLowersBaselineClearColumn_IndexAndPkOmitted(ColumnAccessLevel level)
    {
        var view = await RestAsUserAsync(level);

        // "amount" is baseline-clear in the catalog; the caller's consent denies it, so only the index over "id" survives.
        view.Indexes.ShouldBe("id");
        view.PrimaryKeys.ShouldBe(0);
        view.AnyPkFlag.ShouldBeFalse();
    }

    [Fact]
    public async Task I1105_T_Rest_NonAdmin_ConsentMasksKeyColumn_IndexAndPkKept()
    {
        var view = await RestAsUserAsync(ColumnAccessLevel.Mask);

        // #1105: "amount" is masked for this caller, not denied; its key and index stay listed (unique first).
        view.Indexes.ShouldBe("id|amount");
        view.PrimaryKeys.ShouldBe(2);
        view.AnyPkFlag.ShouldBeTrue();
    }

    [Fact]
    public async Task I1105_S01_DeniedKeyColumn_KeyAndIndexesOmittedWhole_Mcp()
    {
        var mcp = await Mcp(Table(), emailLevel: ColumnAccessLevel.Deny).DescribeDatasetAsync(User(), "sales.public.orders");

        // "email" is denied: the primary key (id, email), the unique index (id, email) and the index (email) are omitted whole; no subset.
        Columns(mcp.Indexes).ShouldBe("id,amount");
        mcp.PrimaryKeyOrder.ShouldBeNull();
        mcp.Columns.ShouldAllBe(c => !c.PrimaryKey);
        DatasetIndexProjection.VisibleSet(new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Deny }).ShouldBeEmpty();
        DatasetIndexProjection.VisibleSet(null).ShouldBeEmpty();
    }

    [Fact]
    public async Task I1105_S02_DiscoveryHiddenKeyColumn_KeyAndIndexesOmittedWhole_EvenWhenClear()
    {
        var hidden = Table() with
        {
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" },
                new TableColumn { ColumnName = "email", DataType = "varchar", DiscoveryHidden = true }
            ]
        };

        // Even a caller for whom the hidden column is Clear gets no key or index that names it.
        var projected = DatasetIndexProjection.Project(hidden, new HashSet<string>(["id", "amount", "email"], StringComparer.OrdinalIgnoreCase));
        projected.PrimaryKeyColumns.ShouldBeEmpty();
        Columns(projected.Indexes).ShouldBe("id,amount");

        var mcp = await Mcp(hidden, emailLevel: ColumnAccessLevel.Clear).DescribeDatasetAsync(User(), "sales.public.orders");
        (mcp.Indexes ?? []).ShouldAllBe(i => !i.Columns.Contains("email"));
        mcp.PrimaryKeyOrder.ShouldBeNull();
        mcp.Columns.ShouldAllBe(c => !c.PrimaryKey);
    }

    [Fact]
    public void I1105_S03_MaskedKeyColumn_FilterOrderAndJoinStillRefused_FilterOnMaskedColumn()
    {
        var table = Table();
        // The key (id, email) is listed for this caller (email masked) ...
        DatasetIndexProjection.Project(table, DatasetIndexProjection.VisibleSet(new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["amount"] = ColumnAccessLevel.Clear,
            ["email"] = ColumnAccessLevel.Mask
        })).PrimaryKeyColumns.ShouldBe(new[] { "id", "email" });

        // ... but the masked key column still cannot be filtered on, ordered by or joined on, so the key order is no ranking oracle.
        var meta = table with
        {
            Table = new Table { SourceName = "sales", SchemaName = "public", TableName = "orders", SourceType = "PostgreSql" },
            ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["email"] = new MaskingRule { RuleType = "REDACT", Replacement = "***" } }
        };
        var decision = TableAccessDecision.Allowed(Orders,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["email"] = ColumnAccessLevel.Mask },
            hasUnconstrainedColumnAllow: true);
        var access = new Dictionary<TableIdentifier, Autheris.Application.Sql.Tree.TreeTableAccess> { [Orders] = new(meta, decision) };

        var filtered = new Autheris.Application.Sql.Tree.TreeQueryNode(Orders, ["id"])
        {
            Where = new Autheris.Application.Sql.Tree.TreeComparison("email", Autheris.Application.Sql.Tree.TreeFilterOperator.Eq, "a@b.c")
        };
        var sorted = new Autheris.Application.Sql.Tree.TreeQueryNode(Orders, ["id"]) { OrderBy = [new Autheris.Application.Sql.Tree.TreeOrder("email")] };
        var joined = new Autheris.Application.Sql.Tree.TreeQueryNode(Orders, ["id"])
        {
            Relations = [new Autheris.Application.Sql.Tree.TreeRelationNode("self", ParentColumns: ["email"], ChildColumns: ["email"], IsList: true,
                new Autheris.Application.Sql.Tree.TreeQueryNode(Orders, ["id"]))]
        };

        foreach (var node in new[] { filtered, sorted, joined })
        {
            var ex = Should.Throw<Autheris.Domain.Exceptions.FilterOnMaskedColumnException>(() =>
                Autheris.Application.Sql.Tree.TreeSqlCompiler.Compile(node, access, Autheris.Domain.Common.DatabaseDialect.PostgreSql));
            ex.ErrorCode.ShouldBe("FILTER_ON_MASKED_COLUMN");
        }
    }

    [Fact]
    public void I1105_S04_RestrictedScope_DiscoveryKeepsKeysOfScopeMaskedColumns_OmitsDenied_AndOffersNoUnmaskRequest()
    {
        var series = new TableMetadata
        {
            Identifier = Orders,
            Table = new Table { TableName = "orders", SchemaName = "public", IsActive = true },
            PrimaryKeyColumns = ["client_id", "ts"],
            Indexes = [new TableIndexDefinition("ix_ts", ["ts"], false)],
            Columns =
            [
                new TableColumn { ColumnName = "client_id", DataType = "int" },
                new TableColumn { ColumnName = "ts", DataType = "datetime2" },
                new TableColumn { ColumnName = "latitude", DataType = "float" }
            ]
        }.WithPii("client_id", "ts", "latitude");

        // The person's base rule is Clear on every column; the restricted token scope (Agent.Read) has no scope-specific Clear.
        TableAccessDecision Decision(ColumnAccessLevel tsLevel) => new(
            Orders,
            IsAllowed: true,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["client_id"] = ColumnAccessLevel.Clear,
                ["ts"] = tsLevel,
                ["latitude"] = ColumnAccessLevel.Clear
            },
            CombinedRowFilterSql: null,
            DeniedReasons: [])
        {
            ScopeClearance = new RestrictedScopeClearance(
                new HashSet<string>(["Agent.Read"], StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))
        };

        var masked = Autheris.Application.Catalog.Discovery.DiscoveryAccessMapper.Map(
            series, Decision(ColumnAccessLevel.Clear), [], isPendingRequest: false, isRecentlyExpired: false, artifacts: null, language: null, isHidden: false);

        masked.Columns.ShouldAllBe(c => c.State == DiscoveryColumnState.Masked);
        masked.QueryShape!.PrimaryKeyColumns.ShouldBe(new[] { "client_id", "ts" });
        Columns(masked.QueryShape.Indexes).ShouldBe("ts");
        // #1106: masked only by the token scope; the same person reads the columns in clear, so no unmask request is offered.
        masked.Columns.ShouldAllBe(c => c.ClearRequestable.Count == 0);

        var denied = Autheris.Application.Catalog.Discovery.DiscoveryAccessMapper.Map(
            series, Decision(ColumnAccessLevel.Deny), [], isPendingRequest: false, isRecentlyExpired: false, artifacts: null, language: null, isHidden: false);

        denied.Columns.Single(c => c.Name == "ts").State.ShouldBe(DiscoveryColumnState.NoAccess);
        denied.QueryShape!.PrimaryKeyColumns.ShouldBeEmpty();
        denied.QueryShape.Indexes.ShouldBeEmpty();
    }

    [Fact]
    public async Task I404_T_SL2_Rest_ResolverFailure_OmitsEveryKeyAndIndex()
    {
        var view = await RestAsUserAsync(ColumnAccessLevel.Clear, new ThrowingResolution());

        view.Indexes.ShouldBeEmpty();
        view.PrimaryKeys.ShouldBe(0);
        view.AnyPkFlag.ShouldBeFalse();
    }

    private sealed class ThrowingResolution :IConsentResolutionService
    {
        public TableAccessDecision ResolveAccess(
            Sid userSid,
            IReadOnlySet<Sid> subjectGroupSids,
            IReadOnlySet<string> userRoles,
            TableIdentifier table,
            IReadOnlyList<Consent> activeConsents,
            Autheris.Domain.Common.DatabaseDialect dialect = Autheris.Domain.Common.DatabaseDialect.SqlServer,
            IReadOnlySet<Sid>? allUserSids = null,
            IReadOnlySet<string>? callerScopes = null) =>
            throw new InvalidOperationException("resolver down");
    }

    private sealed class DatasetIndexInfoComparer: IEqualityComparer<DatasetIndexInfo>
    {
        public static readonly DatasetIndexInfoComparer Instance = new();

        public bool Equals(DatasetIndexInfo? x, DatasetIndexInfo? y) =>
            x != null && y != null && x.Unique == y.Unique && x.Columns.SequenceEqual(y.Columns);

        public int GetHashCode(DatasetIndexInfo obj) => obj.Columns.Count;
    }

    private static TableMetadata WithDeclaredNonUnique(TableMetadata table) => table with
    {
        Indexes =
        [
            .. table.Indexes,
            new TableIndexDefinition("ix_amount_desc", ["amount"], false, [KeyColumnDirection.Descending])
        ]
    };

    [Fact]
    public async Task I404_T_34_Describe_JsonHasNoIndexName()
    {
        var mcp = await Mcp(WithDeclaredNonUnique(Table())).DescribeDatasetAsync(User(), "sales.public.orders");

        var json = System.Text.Json.JsonSerializer.Serialize(mcp.Indexes);

        json.ShouldNotContain("ix_");
        json.ShouldNotContain("ux_");
        json.ShouldNotContain("\"name\"", Case.Insensitive);
    }

    [Fact]
    public void I404_T_S11_S12_DedupAndCap_AfterVisibility_NoCountSideChannel_AndNoCrossCallerState()
    {
        var table = WithDeclaredNonUnique(Table());
        var all = new HashSet<string>(["id", "amount", "email"], StringComparer.OrdinalIgnoreCase);
        var noEmail = new HashSet<string>(["id", "amount"], StringComparer.OrdinalIgnoreCase);

        var visibleToPrivileged = DatasetIndexProjection.Project(table, all);
        var visibleToOther = DatasetIndexProjection.Project(table, noEmail);
        var again = DatasetIndexProjection.Project(table, noEmail);

        visibleToPrivileged.Indexes.Count.ShouldBeGreaterThan(visibleToOther.Indexes.Count);
        Columns(visibleToOther.Indexes).ShouldBe("id,amount|amount");
        Columns(again.Indexes).ShouldBe(Columns(visibleToOther.Indexes), "the projection is a pure function of table and clear set");

        // Hidden declared indexes do not consume cap slots.
        var many = Enumerable.Range(0, 40).Select(n => new TableIndexDefinition($"ix_hidden_{n}", ["email", "c" + n], false)).ToList();
        var capped = table with { Indexes = [.. many, new TableIndexDefinition("ix_amount", ["amount"], false)] };
        Columns(DatasetIndexProjection.Project(capped, noEmail).Indexes).ShouldBe("amount");
    }

    [Fact]
    public void I404_T_28_DeclaredAndPhysical_OrderedUniqueFirst_DedupedAndCapped()
    {
        var names = Enumerable.Range(0, 40).Select(i => $"c{i}").ToArray();
        var extraNonUnique = names.Select((n, i) => (n, i)).Where(x => x.i != 1 && x.i != 2).Select(x => new TableIndexDefinition($"ix_{x.i}", [x.n], false));
        var table = new TableMetadata
        {
            Identifier = Orders,
            Table = new Table { TableName = "orders", SchemaName = "public" },
            Columns = names.Select(n => new TableColumn { ColumnName = n, DataType = "int" }).ToList(),
            Indexes = [new TableIndexDefinition("n1", ["c1"], false), new TableIndexDefinition("u2", ["c2"], true), .. extraNonUnique]
        };
        var clear = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

        var projected = DatasetIndexProjection.Project(table, clear);

        projected.Indexes.Count.ShouldBe(32);
        projected.Indexes[0].Columns.ShouldBe(["c2"]);
        projected.Indexes[0].Unique.ShouldBeTrue();
        projected.Indexes[1].Columns.ShouldBe(["c1"]);
        projected.Indexes.Skip(2).ShouldAllBe(x => !x.Unique);
        projected.Indexes.Select(x => string.Join(",", x.Columns)).Distinct().Count().ShouldBe(32);
    }

    private sealed class StubGoldenQueryService : IGoldenQueryService
    {
        public static readonly StubGoldenQueryService Instance = new();
        public void RegisterGoldenQuery(GoldenQuery query) { }
        public ValueTask<IReadOnlyList<GoldenQuery>> GetGoldenQueriesAsync(string? domain, string? tableName, Func<GoldenQuery, bool>? visible, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<GoldenQuery>>([]);
        public ValueTask<IReadOnlyList<GoldenQuery>> GetGoldenQueriesAsync(GoldenQueryReadContext context, string? domain, string? tableName, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<GoldenQuery>>([]);
        public ValueTask<GoldenQuery?> GetByIdAsync(string id, CancellationToken ct = default)
            => ValueTask.FromResult<GoldenQuery?>(null);
    }

    private sealed class StubGraphQlCatalogMap : IGraphQlCatalogMap
    {
        public static readonly StubGraphQlCatalogMap Instance = new();
        public Task<GraphQlTableMapping?> GetTableAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult<GraphQlTableMapping?>(null);
        public Task<string?> GetRootFieldAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<TableIdentifier?> ResolveRootFieldAsync(string field, CancellationToken ct = default)
            => Task.FromResult<TableIdentifier?>(null);
        public Task<IReadOnlyList<TableIdentifier>?> ResolveDocumentTablesAsync(string document, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TableIdentifier>?>(null);
    }
}
