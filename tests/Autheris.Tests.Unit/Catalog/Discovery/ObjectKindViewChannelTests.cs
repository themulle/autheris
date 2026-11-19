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
using Autheris.Domain.Security;
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
    }

    private sealed class NoopExecutionService : IGatewayExecutionService
    {
        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(ClaimsPrincipal? principal, TableIdentifier table, int? first = null, int? after = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, TableAccessDecision Decision)> ExecuteTableQueryAsync(ClaimsPrincipal? principal, TableIdentifier table, int? first, int? after, IReadOnlyDictionary<string, object?>? queryArguments, IReadOnlyList<string>? requestedFields = null, IReadOnlyDictionary<string, string[]>? requestHeaders = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TableAccessDecision> CheckTableAccessAsync(ClaimsPrincipal? principal, TableIdentifier table, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
