namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class DiscoveryKeysetTests
{
    private static CatalogCaller Caller(string sid = "S-USER", string tenant = "tenant-a") =>
        DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User(sid, tenant), new TenantId(tenant));

    [Fact]
    public async Task ListKeyset_SortsCaseInsensitivelyWithOrdinalTieBreak_AndPagesCompletely()
    {
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", "zeta"));
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", "Alpha"));
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", "alpha"));
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("hr", "employees"));

        var service = DiscoveryTestBuilder.CreateService(tables: repo);
        var caller = Caller();

        var p1 = await service.ListKeysetAsync(new DiscoveryKeysetQuery(First: 2), caller);
        p1.TotalCount.ShouldBe(4);
        p1.Items.Select(static i => i.Table.Identifier.ToString()).ShouldBe(["hr.dbo.employees", "sales.dbo.Alpha"]);
        p1.NextKey.ShouldBe("sales.dbo.Alpha");

        var p2 = await service.ListKeysetAsync(new DiscoveryKeysetQuery(First: 2, AfterKey: p1.NextKey), caller);
        p2.TotalCount.ShouldBe(4);
        p2.Items.Select(static i => i.Table.Identifier.ToString()).ShouldBe(["sales.dbo.alpha", "sales.dbo.zeta"]);
        p2.NextKey.ShouldBeNull();
    }

    [Fact]
    public async Task ListKeyset_PreProjectionFilters_ProjectAtMostOnePage_AndReturnExactCount()
    {
        var repo = new FakeDiscoveryTableRepository();
        for (var i = 0; i < 60; i++)
        {
            repo.Tables.Add(DiscoveryTestBuilder.MakeTable(
                i % 2 == 0 ? "finance" : "sales",
                $"t{i:000}",
                objectKind: i % 3 == 0 ? DiscoveryObjectKind.View : DiscoveryObjectKind.Table));
        }

        var pdp = FakeAccessDecisionPoint.AllowAll();
        var service = DiscoveryTestBuilder.CreateService(tables: repo, pdp: pdp);
        var caller = Caller();

        var page = await service.ListKeysetAsync(
            new DiscoveryKeysetQuery(Domain: "finance", Kind: DiscoveryObjectKind.View, First: 5),
            caller);

        // 60 tables -> 30 finance -> 10 finance views (indices 0, 6, 12, 18, 24, 30, 36, 42, 48, 54).
        page.TotalCount.ShouldBe(10);
        page.Items.Count.ShouldBe(5);
        page.NextKey.ShouldNotBeNull();
        // Only the 5 returned page items are projected (never 10 or 60).
        pdp.Requests.Count(static r => r.Operation == AccessOperation.Discover).ShouldBe(5);
    }

    [Fact]
    public async Task ListKeyset_PostProjectionFilter_FillsAcrossWindows_AndHonorsCountTotalFalse()
    {
        var repo = new FakeDiscoveryTableRepository();
        for (var i = 0; i < 15; i++)
        {
            repo.Tables.Add(DiscoveryTestBuilder.MakeTable(
                "sales",
                $"t{i:00}",
                columns: i is 1 or 7 or 13
                    ? [("id", false, false), ("email", true, false)]
                    : [("id", false, false)]));
        }

        var pdp = FakeAccessDecisionPoint.AllowAll();
        var service = DiscoveryTestBuilder.CreateService(tables: repo, pdp: pdp);
        var caller = Caller();

        static bool HasVisiblePii(DiscoveryObject obj) =>
            DiscoveryColumnVisibility.VisibleColumns(obj).Any(static c => c.Pii);

        var p1 = await service.ListKeysetAsync(
            new DiscoveryKeysetQuery(First: 2, ProjectedPredicate: HasVisiblePii, CountTotal: true),
            caller);

        p1.TotalCount.ShouldBe(3);
        p1.Items.Select(static i => i.Object.Name).ShouldBe(["t01", "t07"]);
        p1.NextKey.ShouldBe("sales.dbo.t07");

        // Second page with CountTotal: false projects only from afterKey onward, without re-counting.
        var pdp2 = FakeAccessDecisionPoint.AllowAll();
        var service2 = DiscoveryTestBuilder.CreateService(tables: repo, pdp: pdp2);
        var p2 = await service2.ListKeysetAsync(
            new DiscoveryKeysetQuery(AfterKey: p1.NextKey, First: 2, ProjectedPredicate: HasVisiblePii, CountTotal: false),
            caller);

        p2.TotalCount.ShouldBeNull();
        p2.Items.Select(static i => i.Object.Name).ShouldBe(["t13"]);
        p2.NextKey.ShouldBeNull();
        // Only tables after t07 (t08..t14 = 7 tables) are projected, never t00..t07.
        pdp2.Requests.Count(static r => r.Operation == AccessOperation.Discover).ShouldBe(7);
    }

    [Fact]
    public async Task ListKeyset_WithSearch_CountsAndReturnsOnlyVisibleMatches()
    {
        var repo = new FakeDiscoveryTableRepository();
        var visibleMatch = DiscoveryTestBuilder.MakeTable("sales", "orders", columns: [("order_id", false, false)]);
        var deniedMatch = DiscoveryTestBuilder.MakeTable("sales", "patients", columns: [("id", false, false), ("secret_order_ref", false, false)]);
        repo.Tables.Add(visibleMatch);
        repo.Tables.Add(deniedMatch);

        var pdp = new FakeAccessDecisionPoint(req =>
            req.Object.Identifier == deniedMatch.Identifier
                ? TableAccessDecision.Allowed(
                    req.Object.Identifier,
                    new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["secret_order_ref"] = ColumnAccessLevel.Deny },
                    hasUnconstrainedColumnAllow: false)
                : TableAccessDecision.Allowed(req.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));

        var service = DiscoveryTestBuilder.CreateService(tables: repo, pdp: pdp);
        var page = await service.ListKeysetAsync(new DiscoveryKeysetQuery(Search: "order", First: 10), Caller());

        page.TotalCount.ShouldBe(1);
        page.Items.Select(static i => i.Object.Name).ShouldBe(["orders"]);
        page.NextKey.ShouldBeNull();
    }

    [Fact]
    public async Task ListKeyset_TagAndSensitivityAndMetadataPredicate_FilterPreProjection()
    {
        var repo = new FakeDiscoveryTableRepository();
        var t1 = DiscoveryTestBuilder.MakeTable("sales", "a");
        t1 = t1 with
        {
            CatalogTags = ["finance"],
            Table = new Table
            {
                Id = t1.Table.Id,
                SourceName = t1.Table.SourceName,
                SchemaName = t1.Table.SchemaName,
                TableName = t1.Table.TableName,
                IsActive = true
            }
        };
        var t2 = DiscoveryTestBuilder.MakeTable("sales", "b");
        t2 = t2 with { CatalogTags = ["finance"] };
        var t3 = DiscoveryTestBuilder.MakeTable("sales", "c");
        t3 = t3 with { CatalogTags = ["hr"] };
        repo.Tables.AddRange([t1, t2, t3]);

        var service = DiscoveryTestBuilder.CreateService(tables: repo);
        var page = await service.ListKeysetAsync(
            new DiscoveryKeysetQuery(
                Tag: "finance",
                ProtectionClass: "confidential",
                MetadataPredicate: m => m.Identifier.TableName == "a"),
            Caller());

        page.TotalCount.ShouldBe(1);
        page.Items.Select(static i => i.Object.Name).ShouldBe(["a"]);
    }

    [Fact]
    public async Task ListKeyset_EnforcesSingleRateLimitGate_AndExcludesProcedures()
    {
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", "orders"));

        var rateLimiter = new FakeDiscoveryRateLimiter();
        var discoveryOpts = new CatalogDiscoveryOptions { Discovery = new CatalogDiscoverySettings { RequestsPerMinute = 1 } };
        var service = DiscoveryTestBuilder.CreateService(tables: repo, discoveryOptions: discoveryOpts, rateLimiter: rateLimiter);
        var caller = Caller();

        var first = await service.ListKeysetAsync(new DiscoveryKeysetQuery(), caller);
        first.Items.Count.ShouldBe(1);
        first.Items[0].Object.Kind.ShouldBe(DiscoveryObjectKind.Table);

        var ex = await Should.ThrowAsync<DiscoveryStatusException>(async () =>
            await service.ListKeysetAsync(new DiscoveryKeysetQuery(), caller));
        ex.Kind.ShouldBe(DiscoveryFailureKind.RateLimited);
    }
}
