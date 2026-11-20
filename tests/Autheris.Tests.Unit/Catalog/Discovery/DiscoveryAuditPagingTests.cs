namespace Autheris.Tests.Unit.Catalog.Discovery;

using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DiscoveryAuditPagingTests
{
    private static CatalogCaller Caller(string sid = "S-AUDIT-USER", string tenant = "tenant-a") =>
        DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User(sid, tenant), new TenantId(tenant));

    [Fact]
    public async Task Audit_RecordsEveryReturnedObject()
    {
        var repo = new FakeDiscoveryTableRepository();
        for (var i = 0; i < 65; i++)
        {
            repo.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", $"tbl_{i:000}"));
        }

        var audit = new FakeAuditWriter();
        var service = DiscoveryTestBuilder.CreateService(tables: repo, audit: audit);

        var page = await service.ListKeysetAsync(
            new DiscoveryKeysetQuery(Domain: "sales", First: 60),
            Caller());

        page.Items.Count.ShouldBe(60);
        audit.Entries.Count.ShouldBe(1);

        var entry = audit.Entries[0];
        entry.EventType.ShouldBe(AuditEventTypes.CatalogRead);
        using var doc = JsonDocument.Parse(entry.DetailsJson);
        doc.RootElement.GetProperty("objectCount").GetInt32().ShouldBe(60);
        var recordedIds = doc.RootElement.GetProperty("objectIds").EnumerateArray().Select(static x => x.GetString()!).ToList();
        recordedIds.ShouldBe(page.Items.Select(static i => i.Table.Identifier.ToString()).ToList());
        var filters = doc.RootElement.GetProperty("appliedFilters").EnumerateArray().Select(static x => x.GetString()!).ToList();
        filters.ShouldContain("domain");
    }

    [Fact]
    public async Task Audit_DoesNotRecordFilteredOutObjects()
    {
        var repo = new FakeDiscoveryTableRepository();
        for (var i = 0; i < 12; i++)
        {
            repo.Tables.Add(DiscoveryTestBuilder.MakeTable(
                "sales",
                $"obj_{i:00}",
                columns: i is 2 or 9
                    ? [("id", false, false), ("email", true, false)]
                    : [("id", false, false)]));
        }

        var audit = new FakeAuditWriter();
        var service = DiscoveryTestBuilder.CreateService(tables: repo, audit: audit);

        static bool VisiblePii(DiscoveryObject obj) =>
            DiscoveryColumnVisibility.VisibleColumns(obj).Any(static c => c.Pii);

        var page = await service.ListKeysetAsync(
            new DiscoveryKeysetQuery(First: 1, ProjectedPredicate: VisiblePii, CountTotal: true, AppliedFilterNames: ["pii"]),
            Caller());

        page.TotalCount.ShouldBe(2);
        page.Items.Select(static i => i.Object.Name).ShouldBe(["obj_02"]);
        audit.Entries.Count.ShouldBe(1);

        using var doc = JsonDocument.Parse(audit.Entries[0].DetailsJson);
        doc.RootElement.GetProperty("objectCount").GetInt32().ShouldBe(1);
        var recordedIds = doc.RootElement.GetProperty("objectIds").EnumerateArray().Select(static x => x.GetString()!).ToList();
        recordedIds.ShouldBe(["sales.dbo.obj_02"]);
        doc.RootElement.GetProperty("appliedFilters").EnumerateArray().Select(static x => x.GetString()!).ShouldContain("pii");
    }

    [Fact]
    public async Task Facets_WritesOneAuditEvent()
    {
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("finance_secret_name", "ledger_secret_table", schema: "private_schema"));
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", "orders", schema: "dbo"));

        var audit = new FakeAuditWriter();
        var service = DiscoveryTestBuilder.CreateService(tables: repo, audit: audit);

        var facets = await service.FacetsAsync(DiscoveryAccessState.None, Caller());

        facets.TotalCount.ShouldBe(2);
        audit.Entries.Count.ShouldBe(1);

        var entry = audit.Entries[0];
        entry.EventType.ShouldBe(AuditEventTypes.CatalogRead);
        entry.TargetTable.ShouldBe("*");
        entry.DetailsJson.ShouldNotContain("finance_secret_name");
        entry.DetailsJson.ShouldNotContain("ledger_secret_table");
        entry.DetailsJson.ShouldNotContain("private_schema");

        using var doc = JsonDocument.Parse(entry.DetailsJson);
        doc.RootElement.GetProperty("operation").GetString().ShouldBe("facets");
        doc.RootElement.GetProperty("totalCount").GetInt32().ShouldBe(2);
        doc.RootElement.GetProperty("domainCount").GetInt32().ShouldBe(2);
        doc.RootElement.GetProperty("schemaCount").GetInt32().ShouldBe(2);
        doc.RootElement.GetProperty("accessStatus").GetString().ShouldBe("none");
    }
}
