namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.External;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog.Discovery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class ExternalCatalogOneSearchTests
{
    private const string Tenant = "tenant-a";
    private const string UserSid = "S-1-ONE-SEARCH";

    private static ClaimsPrincipal User() =>
        ExternalCatalogTestHarness.BuildDelegatedUserPrincipal(Tenant, UserSid, scopes: "Apps.Read");

    private static List<string?> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("objectId").GetString()).ToList();

    [Fact]
    public async Task CatalogObjects_MultiTermQuery_MatchesNonContiguousTerms_AndMatchesFacets()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.AddTable("sales", "auftrags_position", columns: [("kunden_nr", false, false)]);
        harness.AddTable("hr", "mitarbeiter", columns: [("personal_id", false, false)]);

        var page = (await harness.ListObjectsAsync(User(), q: "auftrag kunde")).ReadJson();
        Ids(page).ShouldBe(["sales.dbo.auftrags_position"]);
        page.GetProperty("totalCount").GetInt32().ShouldBe(1);

        var facets = (await harness.GetFacetsAsync(User(), q: "auftrag kunde")).ReadJson();
        facets.GetProperty("totalCount").GetInt32().ShouldBe(1);
        facets.GetProperty("domains").EnumerateArray().ShouldHaveSingleItem().GetProperty("domain").GetString().ShouldBe("sales");
    }

    [Fact]
    public async Task CatalogObjects_UsesSearchIndex_SupportsRelevanceSortAndCursorBinding()
    {
        var tables = new FakeDiscoveryTableRepository();
        var t1 = DiscoveryTestBuilder.MakeTable("sales", "alpha_orders", ownerTenantId: Tenant);
        var t2 = DiscoveryTestBuilder.MakeTable("sales", "beta_orders", ownerTenantId: Tenant);
        var t3 = DiscoveryTestBuilder.MakeTable("hr", "gamma_orders", ownerTenantId: Tenant);
        tables.Tables.AddRange([t1, t2, t3]);

        var searchEngine = Substitute.For<ICatalogSearchEngine>();
        searchEngine.IsIndexReady.Returns(true);
        searchEngine.SearchAsync(
                Arg.Any<CatalogSearchQuery>(),
                Arg.Any<Func<TableIdentifier, bool>>(),
                Arg.Any<Func<TableIdentifier, bool>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var pred = callInfo.ArgAt<Func<TableIdentifier, bool>>(1);
                var hits = new[]
                {
                    new CatalogSearchHit(t3.Identifier, "hr", "Gamma", null, "en", 0.99, null, 0.99, ["orders"], [], [], "NORMAL", "GRANTED"),
                    new CatalogSearchHit(t1.Identifier, "sales", "Alpha", null, "en", 0.70, null, 0.70, ["orders"], [], [], "NORMAL", "GRANTED"),
                    new CatalogSearchHit(t2.Identifier, "sales", "Beta", null, "en", 0.50, null, 0.50, ["orders"], [], [], "NORMAL", "GRANTED")
                };
                return hits.Where(h => pred(h.TableIdentifier)).ToList();
            });

        var opts = Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() });
        var discovery = new DiscoveryService(
            tables,
            FakeAccessDecisionPoint.AllowAll(),
            new FakeDiscoveryConsentRepository(),
            new FakeDiscoveryApprovalRepository(),
            new FakeDiscoveryOwnershipRepository(),
            new FakeDiscoveryArtifactStore(),
            new FakeAuditWriter(),
            opts,
            Options.Create(new CatalogDiscoveryOptions()),
            searchEngine: searchEngine,
            logger: NullLogger<DiscoveryService>.Instance);

        var reader = new ExternalCatalogReader(
            tables,
            discovery,
            new FakeDiscoveryConsentRepository(),
            new FakeDiscoveryOwnershipRepository(),
            Substitute.For<Autheris.Application.ConsentRequests.IConsentWorkflowConfigRepository>(),
            Substitute.For<ICatalogChangeStore>(),
            opts);

        var user = User();
        var tenantId = new TenantId(Tenant);

        // Relevance sort page 1 (size 2) -> hr.dbo.gamma_orders, sales.dbo.alpha_orders
        var p1 = await reader.ListObjectsAsync(user, tenantId, new ExternalCatalogListQuery(Q: "orders", Mode: "keyword", Sort: "relevance", PageSize: 2));
        p1.IsSuccess.ShouldBeTrue();
        p1.Value!.TotalCount.ShouldBe(3);
        p1.Value.Items.Select(i => i.ObjectId).ShouldBe(["hr.dbo.gamma_orders", "sales.dbo.alpha_orders"]);
        p1.Value.NextCursor.ShouldNotBeNull();

        // Relevance sort page 2 -> sales.dbo.beta_orders
        var p2 = await reader.ListObjectsAsync(user, tenantId, new ExternalCatalogListQuery(Q: "orders", Mode: "keyword", Sort: "relevance", Cursor: p1.Value.NextCursor, PageSize: 2));
        p2.IsSuccess.ShouldBeTrue();
        p2.Value!.Items.Select(i => i.ObjectId).ShouldBe(["sales.dbo.beta_orders"]);
        p2.Value.NextCursor.ShouldBeNull();

        // Replaying relevance cursor with sort=id must fail with INVALID_CURSOR
        var mismatch = await reader.ListObjectsAsync(user, tenantId, new ExternalCatalogListQuery(Q: "orders", Mode: "keyword", Sort: "id", Cursor: p1.Value.NextCursor, PageSize: 2));
        mismatch.IsSuccess.ShouldBeFalse();
        mismatch.Error!.Code.ShouldBe("INVALID_CURSOR");

        // Invalid sort / mode must fail with 400 INVALID_QUERY
        var badSort = await reader.ListObjectsAsync(user, tenantId, new ExternalCatalogListQuery(Q: "orders", Sort: "bogus"));
        badSort.IsSuccess.ShouldBeFalse();
        badSort.Error!.StatusCode.ShouldBe(400);

        var badMode = await reader.ListObjectsAsync(user, tenantId, new ExternalCatalogListQuery(Q: "orders", Mode: "bogus"));
        badMode.IsSuccess.ShouldBeFalse();
        badMode.Error!.StatusCode.ShouldBe(400);

        // Facets with q and mode match the filtered count
        var facets = await reader.GetFacetsAsync(user, tenantId, accessStatus: null, q: "orders", mode: "keyword");
        facets.IsSuccess.ShouldBeTrue();
        facets.Value!.TotalCount.ShouldBe(3);
    }

    [Fact]
    public async Task McpListDatasets_MultiTermSearch_MatchesConsistentlyWithCatalogSearch()
    {
        var tables = new FakeDiscoveryTableRepository();
        var t1 = DiscoveryTestBuilder.MakeTable("sales", "auftrags_position", ownerTenantId: Tenant, columns: [("kunden_nr", false, false)]);
        var t2 = DiscoveryTestBuilder.MakeTable("hr", "mitarbeiter", ownerTenantId: Tenant, columns: [("personal_id", false, false)]);
        tables.Tables.AddRange([t1, t2]);

        var opts = Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() });
        var consents = new FakeDiscoveryConsentRepository();
        var discovery = new DiscoveryService(
            tables,
            FakeAccessDecisionPoint.AllowAll(),
            consents,
            new FakeDiscoveryApprovalRepository(),
            new FakeDiscoveryOwnershipRepository(),
            new FakeDiscoveryArtifactStore(),
            new FakeAuditWriter(),
            opts,
            Options.Create(new CatalogDiscoveryOptions()),
            logger: NullLogger<DiscoveryService>.Instance);

        var mcp = new McpDatasetCatalog(
            tables,
            consents,
            Substitute.For<IGatewayExecutionService>(),
            options: opts,
            discoveryService: discovery);

        var res = await mcp.ListDatasetsAsync(User(), search: "auftrag kunde", domain: null);
        res.Datasets.ShouldHaveSingleItem().Id.ShouldBe("sales.dbo.auftrags_position");
    }

    [Fact]
    public async Task McpListDatasets_WhenSearchIndexIsReadyButEmpty_FallsBackToVisibleTextMatching()
    {
        var tables = new FakeDiscoveryTableRepository();
        tables.Tables.Add(DiscoveryTestBuilder.MakeTable("default", "mcp_cranes", ownerTenantId: Tenant));

        var emptyReadyIndex = Substitute.For<ICatalogSearchEngine>();
        emptyReadyIndex.IsIndexReady.Returns(true);
        emptyReadyIndex.IndexedTableCount.Returns(0);

        var opts = Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() });
        var consents = new FakeDiscoveryConsentRepository();
        var discovery = new DiscoveryService(
            tables,
            FakeAccessDecisionPoint.AllowAll(),
            consents,
            new FakeDiscoveryApprovalRepository(),
            new FakeDiscoveryOwnershipRepository(),
            new FakeDiscoveryArtifactStore(),
            new FakeAuditWriter(),
            opts,
            Options.Create(new CatalogDiscoveryOptions()),
            logger: NullLogger<DiscoveryService>.Instance,
            searchEngine: emptyReadyIndex);

        var mcp = new McpDatasetCatalog(
            tables,
            consents,
            Substitute.For<IGatewayExecutionService>(),
            options: opts,
            discoveryService: discovery);

        var res = await mcp.ListDatasetsAsync(User(), search: "crane", domain: null);
        res.Datasets.ShouldHaveSingleItem().Id.ShouldBe("default.dbo.mcp_cranes");
    }
}
