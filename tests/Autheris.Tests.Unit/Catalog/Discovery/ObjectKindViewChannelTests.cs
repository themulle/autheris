namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>Issue #896: an object registered as a view reports kind "view" on every channel, and the kind never changes an access decision.</summary>
public sealed class ObjectKindViewChannelTests
{
    private static FakeAccessDecisionPoint Pdp() => new(r =>
        TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

    private static TableMetadata ViewOf(string name, DiscoveryObjectKind kind) =>
        DiscoveryTestBuilder.MakeTable("sales", name, columns: [("id", false, false)], objectKind: kind);

    [Fact]
    [Trait("Category", "Security")]
    public void Map_ViewAndTableWithEqualGrants_ProduceIdenticalDecisionsApartFromTheKind()
    {
        var decision = TableAccessDecision.Allowed(
            new TableIdentifier("sales", "dbo", "orders"),
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["id"] = ColumnAccessLevel.Mask },
            rowFilterSql: "region = 'EU'",
            hasUnconstrainedColumnAllow: true);
        var table = ViewOf("orders", DiscoveryObjectKind.Table);
        var view = ViewOf("orders", DiscoveryObjectKind.View);

        var mappedTable = DiscoveryAccessMapper.Map(table, decision, [], false, false, null, language: null, isHidden: false);
        var mappedView = DiscoveryAccessMapper.Map(view, decision, [], false, false, null, language: null, isHidden: false);

        mappedView.Kind.ShouldBe(DiscoveryObjectKind.View);
        mappedTable.Kind.ShouldBe(DiscoveryObjectKind.Table);
        mappedView.Access.ShouldBe(mappedTable.Access);
        mappedView.RowScope.ShouldBe(mappedTable.RowScope);
        mappedView.SamplesAvailable.ShouldBe(mappedTable.SamplesAvailable);
        JsonSerializer.Serialize(mappedView with { Kind = DiscoveryObjectKind.Table })
            .ShouldBe(JsonSerializer.Serialize(mappedTable));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Pdp_RequestsForViewAndTable_CarryNoKindDifference()
    {
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(ViewOf("v_orders", DiscoveryObjectKind.View));
        repo.Tables.Add(ViewOf("t_orders", DiscoveryObjectKind.Table));
        var pdp = Pdp();
        var service = DiscoveryTestBuilder.CreateService(tables: repo, pdp: pdp);
        var caller = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-USER", "tenant-a"));

        var page = await service.ListAsync(new DiscoveryQuery(Surface: DiscoverySurface.Rest), caller);

        var viewRequests = pdp.Requests.Where(r => r.Object.Identifier.TableName == "v_orders").Select(r => (r.Operation, r.Transport)).ToList();
        var tableRequests = pdp.Requests.Where(r => r.Object.Identifier.TableName == "t_orders").Select(r => (r.Operation, r.Transport)).ToList();
        viewRequests.ShouldNotBeEmpty();
        viewRequests.ShouldBe(tableRequests);

        var view = page.Items.Single(i => i.Name == "v_orders");
        var table = page.Items.Single(i => i.Name == "t_orders");
        view.Kind.ShouldBe(DiscoveryObjectKind.View);
        table.Kind.ShouldBe(DiscoveryObjectKind.Table);
        view.Access.ShouldBe(table.Access);
        view.Columns.Select(c => c.State).ShouldBe(table.Columns.Select(c => c.State));
    }

    [Fact]
    public async Task ViewObject_ReportsViewOnGetSearchAndSearchHit()
    {
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(ViewOf("v_orders", DiscoveryObjectKind.View));
        var service = DiscoveryTestBuilder.CreateService(tables: repo, pdp: Pdp());
        var caller = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User("S-USER", "tenant-a"));

        var fetched = await service.GetAsync(new TableIdentifier("sales", "dbo", "v_orders"), caller);
        fetched.ShouldNotBeNull();
        fetched.Kind.ShouldBe(DiscoveryObjectKind.View);

        var searched = await service.SearchAsync("v_orders", new DiscoverySearchOptions(Surface: DiscoverySurface.Search), caller);
        var hit = searched.ShouldHaveSingleItem();
        hit.Kind.ShouldBe(DiscoveryObjectKind.View);
        CatalogDiscoveryProjection.ToSearchHit(hit, null, null).Kind.ShouldBe("view");
    }

    [Fact]
    public async Task McpListDatasets_ViewObject_ReportsKindViewOnDiscoveryAndLegacyPaths()
    {
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(ViewOf("v_orders", DiscoveryObjectKind.View));
        repo.Tables.Add(ViewOf("t_orders", DiscoveryObjectKind.Table));
        var caller = DiscoveryTestBuilder.User("S-USER", "tenant-a");

        var discovery = DiscoveryTestBuilder.CreateService(tables: repo, pdp: Pdp());
        var viaDiscovery = new McpDatasetCatalog(repo, new FakeDiscoveryConsentRepository(), new NoopExecutionService(), discoveryService: discovery);
        var list = await viaDiscovery.ListDatasetsAsync(caller);
        list.Datasets.Single(d => d.Id.EndsWith("v_orders", StringComparison.OrdinalIgnoreCase)).Kind.ShouldBe("view");
        list.Datasets.Single(d => d.Id.EndsWith("t_orders", StringComparison.OrdinalIgnoreCase)).Kind.ShouldBe("table");

        var legacy = new McpDatasetCatalog(
            repo, new FakeDiscoveryConsentRepository(), new NoopExecutionService(),
            options: Options.Create(new GatewayOptions { Insecure = new InsecureGettingStartedOptions { danger_bypass_mcp_auth = true } }));
        var legacyList = await legacy.ListDatasetsAsync(caller);
        legacyList.Datasets.Single(d => d.Id.EndsWith("v_orders", StringComparison.OrdinalIgnoreCase)).Kind.ShouldBe("view");
        legacyList.Datasets.Single(d => d.Id.EndsWith("t_orders", StringComparison.OrdinalIgnoreCase)).Kind.ShouldBe("table");
    }

    private static TableMetadata MakeMetricView(string name = "sales_mv")
    {
        var table = DiscoveryTestBuilder.MakeTable("sales", name, columns: [("region", false, false), ("revenue", false, false)], objectKind: DiscoveryObjectKind.MetricView);
        var cols = new List<TableColumn>
        {
            new() { Id = table.Columns[0].Id, TableId = table.Table.Id, ColumnName = "region", DataType = "string", Role = MetricViewColumnRole.Dimension },
            new() { Id = table.Columns[1].Id, TableId = table.Table.Id, ColumnName = "revenue", DataType = "decimal(18,2)", Role = MetricViewColumnRole.Measure }
        };
        return table with { Columns = cols };
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ObjectGrantOnly_MeasureIsNoAccess()
    {
        var mv = MakeMetricView();
        var objectGrantOnly = TableAccessDecision.Allowed(mv.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        var effective = Autheris.Application.Policy.Pdp.AccessDecisionPoint.ApplyMetricViewMeasureDefaults(objectGrantOnly, mv);
        var mapped = DiscoveryAccessMapper.Map(mv, effective, [], false, false, null, language: null, isHidden: false);

        mapped.Kind.ShouldBe(DiscoveryObjectKind.MetricView);
        mapped.Columns.Single(c => c.Name == "region").State.ShouldBe(DiscoveryColumnState.Clear);
        mapped.Columns.Single(c => c.Name == "revenue").State.ShouldBe(DiscoveryColumnState.NoAccess);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void MeasureGrant_MeasureIsClear()
    {
        var mv = MakeMetricView();
        var withMeasureGrant = TableAccessDecision.Allowed(
            mv.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["revenue"] = ColumnAccessLevel.Clear },
            hasUnconstrainedColumnAllow: true);

        var effective = Autheris.Application.Policy.Pdp.AccessDecisionPoint.ApplyMetricViewMeasureDefaults(withMeasureGrant, mv);
        var mapped = DiscoveryAccessMapper.Map(mv, effective, [], false, false, null, language: null, isHidden: false);

        mapped.Columns.Single(c => c.Name == "region").State.ShouldBe(DiscoveryColumnState.Clear);
        mapped.Columns.Single(c => c.Name == "revenue").State.ShouldBe(DiscoveryColumnState.Clear);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DeniedMeasure_NameAbsentOnRestMcpTrino()
    {
        var mv = MakeMetricView();
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(mv);
        var pdp = new FakeAccessDecisionPoint(r =>
            Autheris.Application.Policy.Pdp.AccessDecisionPoint.ApplyMetricViewMeasureDefaults(
                TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
                mv));
        var discovery = DiscoveryTestBuilder.CreateService(tables: repo, pdp: pdp);
        var callerUser = DiscoveryTestBuilder.User("S-USER", "tenant-a");
        var caller = DiscoveryTestBuilder.Caller(callerUser);

        var restObj = await discovery.GetAsync(mv.Identifier, caller);
        restObj.ShouldNotBeNull();
        DiscoveryColumnVisibility.VisibleColumns(restObj).Select(c => c.Name).ShouldBe(["region"]);
        DiscoveryColumnVisibility.VisibleColumns(restObj).ShouldNotContain(c => c.Name == "revenue");

        var mcp = new McpDatasetCatalog(repo, new FakeDiscoveryConsentRepository(), new NoopExecutionService(), discoveryService: discovery);
        var desc = await mcp.DescribeDatasetAsync(callerUser, "sales.dbo.sales_mv");
        desc.Kind.ShouldBe("metric_view");
        desc.Columns.Select(c => c.Name).ShouldBe(["region"]);
        desc.Columns.Single().Role.ShouldBe("dimension");

        var allowedCols = new HashSet<string>(["region", "revenue"], StringComparer.OrdinalIgnoreCase);
        var jdbcRows = Autheris.Application.Sql.Services.TrinoJdbcMetadataShapes.BuildForTest(
            Autheris.Application.Sql.Services.TrinoJdbcView.Tables,
            [new Autheris.Application.Sql.Services.TrinoJdbcEntry(restObj, Accessible: true, allowedCols)]);
        jdbcRows.Single()[3].ShouldBe("VIEW");

        var jdbcCols = Autheris.Application.Sql.Services.TrinoJdbcMetadataShapes.BuildForTest(
            Autheris.Application.Sql.Services.TrinoJdbcView.Columns,
            [new Autheris.Application.Sql.Services.TrinoJdbcEntry(restObj, Accessible: true, allowedCols)]);
        jdbcCols.Select(r => (string?)r[3]).ShouldBe(["region"]);
    }

    [Fact]
    public async Task GraphQl_MetricView_NotExposed()
    {
        var mv = MakeMetricView();
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(mv);
        var relRepo = NSubstitute.Substitute.For<ITableRelationRepository>();
        var model = await Autheris.GraphQL.Catalog.CatalogSchemaModel.BuildAsync(
            repo, relRepo, logger: null, dialectRegistry: null, crossSourceEnabled: false, tableFilter: null,
            fingerprintCache: null, epochService: null, timeProvider: null);
        model.Tables.ShouldBeEmpty();
    }

    [Fact]
    public void OData_MetricView_NotExposed()
    {
        var mv = MakeMetricView();
        Autheris.Application.OData.ODataHandler.IsExposableObjectKind(mv.Table.ObjectKind).ShouldBeFalse();
        Autheris.Application.OData.ODataHandler.IsExposableObjectKind(DiscoveryObjectKind.Table).ShouldBeTrue();
    }

    private sealed class NoopExecutionService : IGatewayExecutionService
    {
        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(ClaimsPrincipal? principal, TableIdentifier table, int? first = null, int? after = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(ClaimsPrincipal? principal, TableIdentifier table, int? first, int? after, IReadOnlyDictionary<string, object?>? queryArguments, IReadOnlyList<string>? requestedFields = null, IReadOnlyDictionary<string, string[]>? requestHeaders = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TableAccessDecision> CheckTableAccessAsync(ClaimsPrincipal? principal, TableIdentifier table, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
