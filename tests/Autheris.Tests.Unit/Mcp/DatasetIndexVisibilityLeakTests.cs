namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Data.Services;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.OData.Services;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #404 (leak fix): a key or an index over a masked, denied or unresolved column is omitted whole from describe_dataset, the REST
/// catalog detail and the OpenAPI document; no surface emits an index name.
/// </summary>
[Trait("Category", "Security")]
public sealed class DatasetIndexVisibilityLeakTests
{
    private const string Tenant = "tenant-a";
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");

    private static TableMetadata Table() => new()
    {
        Identifier = Orders,
        Table = new Table { TableName = "orders", SchemaName = "public", Sensitivity = "INTERNAL", IsActive = true },
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
            new TableColumn { ColumnName = "email", DataType = "varchar", IsSensitive = true }
        ]
    };

    private static McpDatasetCatalog Mcp(TableMetadata table, IConsentResolutionService? resolution = null)
    {
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([table]));
        var consents = Substitute.For<IConsentRepository>();
        consents.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(
            [
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
                        new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask }
                    ]
                }
            ]));
        var golden = Substitute.For<IGoldenQueryService>();
        golden.GetGoldenQueriesAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<GoldenQuery>>([]));
        return new McpDatasetCatalog(metadata, consents, Substitute.For<IGatewayExecutionService>(), golden,
            Options.Create(new GatewayOptions()), Substitute.For<IGraphQlCatalogMap>(),
            consentResolution: resolution,
            visibilityService: CatalogVisibilityTestFactory.Create(metadata.ServeSingleReadsFromCatalog(), consents));
    }

    private static ClaimsPrincipal User() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", Tenant)], "MCP"));

    private static CatalogDiscoveryService Rest(TableMetadata table)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(table);
        var consents = Substitute.For<IConsentRepository>();
        var options = Wp0.Options(TenancyShape.Single);
        return new CatalogDiscoveryService(
            repo,
            Substitute.For<IAuditLogRepository>(),
            Options.Create(options),
            CatalogVisibilityTestFactory.Create(repo, consents, options, Substitute.For<IRebacEvaluator>()),
            Substitute.For<IKeyVaultSecretProvider>(),
            Substitute.For<IOpenApiIngestionService>(),
            Substitute.For<ICatalogSearchEngine>(),
            NullLogger<CatalogDiscoveryService>.Instance);
    }

    private static RequestContext Admin() => new(new TenantId("t"), new Sid("user:x"), User: Wp0.User("user:x", null, ["GovernanceAdmin"]));

    private static string Columns(IReadOnlyList<DatasetIndexInfo>? indexes) =>
        string.Join("|", (indexes ?? []).Select(i => string.Join(",", i.Columns)));

    [Fact]
    public async Task I404_T_SL1_MaskedColumn_WholeIndexOmitted_Mcp_Rest_GraphQl()
    {
        var mcp = await Mcp(Table()).DescribeDatasetAsync(User(), "sales.public.orders");
        var rest = await Rest(Table()).GetDatasetDetailAsync(Orders, Admin());

        // GraphQL: the MCP GraphQL tool answers through the same DescribeDatasetAsync path (GatewayMcpQueryExecutor), so the
        // describe assertions below cover it; there is no separate GraphQL projection.
        // Parity (I404-SC-L5): both surfaces answer with the single whole, clear-only index.
        Columns(mcp.Indexes).ShouldBe("id,amount");
        Columns(rest!.Indexes).ShouldBe("id,amount");
        // The GraphQL access block of describe_dataset is built from the same description: no key flag over a masked column.
        mcp.Columns.ShouldAllBe(c => !c.PrimaryKey);
        mcp.Indexes!.ShouldAllBe(i => !i.Columns.Contains("email"));
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
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([Table()]));
        var openApi = await new DynamicOpenApiGenerator(repo, NullLogger<DynamicOpenApiGenerator>.Instance).GenerateOpenApiJsonAsync(null);
        openApi.ShouldNotContain("x-autheris-indexes");
        openApi.ShouldNotContain("ix_leaks_email_name");
        openApi.ShouldNotContain("ux_secret_id_email");
        openApi.ShouldNotContain("ix_visible");
    }

    [Fact]
    public async Task I404_T_SL4_PrimaryKeyOrder_OmittedWhenAnyPkColumnMasked_AllSurfaces()
    {
        var mcp = await Mcp(Table()).DescribeDatasetAsync(User(), "sales.public.orders");
        var rest = await Rest(Table()).GetDatasetDetailAsync(Orders, Admin());

        mcp.Columns.ShouldAllBe(c => !c.PrimaryKey);
        rest!.PrimaryKeys.ShouldBeEmpty();
        rest.Columns.ShouldAllBe(c => !c.IsPrimaryKey);
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
            Substitute.For<IAuditLogRepository>(),
            Options.Create(options),
            CatalogVisibilityTestFactory.Create(tables, consents, options, Substitute.For<IRebacEvaluator>()),
            Substitute.For<IKeyVaultSecretProvider>(),
            Substitute.For<IOpenApiIngestionService>(),
            Substitute.For<ICatalogSearchEngine>(),
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
    [InlineData(ColumnAccessLevel.Mask)]
    [InlineData(ColumnAccessLevel.Deny)]
    public async Task I404_T_SL1_Rest_NonAdmin_ConsentLowersBaselineClearColumn_IndexAndPkOmitted(ColumnAccessLevel level)
    {
        var view = await RestAsUserAsync(level);

        // "amount" is baseline-clear in the catalog; the caller's consent lowers it, so only the index over "id" survives.
        view.Indexes.ShouldBe("id");
        view.PrimaryKeys.ShouldBe(0);
        view.AnyPkFlag.ShouldBeFalse();
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

    private static TableMetadata WithPhysical(TableMetadata table) => table with
    {
        PhysicalIndexes =
        [
            new PhysicalIndexDefinition(["email"], []),
            new PhysicalIndexDefinition(["amount"], [KeyColumnDirection.Descending]),
            new PhysicalIndexDefinition(["id", "email"], []),
            new PhysicalIndexDefinition(["id", "amount"], [])   // equals the declared ix_visible: de-duplicated
        ]
    };

    [Fact]
    [Trait("SecurityCondition", "I404-SC-13")]
    public async Task I404_T_S01_PhysicalIndex_OverMaskedColumn_NotDescribed_Mcp_Rest_GraphQl()
    {
        var mcp = await Mcp(WithPhysical(Table())).DescribeDatasetAsync(User(), "sales.public.orders");
        var rest = await Rest(WithPhysical(Table())).GetDatasetDetailAsync(Orders, Admin());

        // GraphQL MCP describe answers through the same DescribeDatasetAsync path (T-32 parity by construction).
        Columns(mcp.Indexes).ShouldBe("id,amount|amount");
        Columns(rest!.Indexes).ShouldBe("id,amount|amount");
        mcp.Indexes!.Last().Unique.ShouldBeFalse();
        mcp.Indexes!.Last().Directions.ShouldBe([KeyColumnDirection.Descending]);
        mcp.Indexes!.ShouldAllBe(x => !x.Columns.Contains("email"));
    }

    [Fact]
    public async Task I404_T_34_Describe_JsonHasNoIndexName()
    {
        var mcp = await Mcp(WithPhysical(Table())).DescribeDatasetAsync(User(), "sales.public.orders");

        var json = System.Text.Json.JsonSerializer.Serialize(mcp.Indexes);

        json.ShouldNotContain("ix_");
        json.ShouldNotContain("ux_");
        json.ShouldNotContain("\"name\"", Case.Insensitive);
    }

    [Fact]
    public void I404_T_S11_S12_DedupAndCap_AfterVisibility_NoCountSideChannel_AndNoCrossCallerState()
    {
        var table = WithPhysical(Table());
        var all = new HashSet<string>(["id", "amount", "email"], StringComparer.OrdinalIgnoreCase);
        var noEmail = new HashSet<string>(["id", "amount"], StringComparer.OrdinalIgnoreCase);

        var visibleToPrivileged = DatasetIndexProjection.Project(table, all);
        var visibleToOther = DatasetIndexProjection.Project(table, noEmail);
        var again = DatasetIndexProjection.Project(table, noEmail);

        visibleToPrivileged.Indexes.Count.ShouldBeGreaterThan(visibleToOther.Indexes.Count);
        Columns(visibleToOther.Indexes).ShouldBe("id,amount|amount");
        Columns(again.Indexes).ShouldBe(Columns(visibleToOther.Indexes), "the projection is a pure function of table and clear set");

        // Hidden physical indexes do not consume cap slots.
        var many = Enumerable.Range(0, 40).Select(n => new PhysicalIndexDefinition(["email", "c" + n], [])).ToList();
        var capped = table with { PhysicalIndexes = [.. many, new PhysicalIndexDefinition(["amount"], [])] };
        Columns(DatasetIndexProjection.Project(capped, noEmail).Indexes).ShouldBe("id,amount|amount");
    }

    [Fact]
    public void I404_T_28_DeclaredAndPhysical_OrderedUniqueFirst_DedupedAndCapped()
    {
        var names = Enumerable.Range(0, 40).Select(i => $"c{i}").ToArray();
        var table = new TableMetadata
        {
            Identifier = Orders,
            Table = new Table { TableName = "orders", SchemaName = "public" },
            Columns = names.Select(n => new TableColumn { ColumnName = n, DataType = "int" }).ToList(),
            Indexes = [new TableIndexDefinition("n1", ["c1"], false), new TableIndexDefinition("u2", ["c2"], true)],
            PhysicalIndexes = names.Select(n => new PhysicalIndexDefinition([n], [])).ToList()
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

    [Fact]
    [Trait("SecurityCondition", "I404-SC-13")]
    public void I404_T_33_Restrict_FiltersPhysicalIndexesAllOrNothing()
    {
        var table = WithPhysical(Table());

        var restricted = TableKeyVisibility.Restrict(table, table.Columns.Where(c => c.ColumnName != "email").ToList());

        restricted.PhysicalIndexes.Select(i => string.Join(",", i.Columns)).ToArray().ShouldBe(["amount", "id,amount"]);
    }
}
