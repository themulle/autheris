namespace Autheris.Tests.Unit.Docs;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Docs.Services;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>Linked documents in discovery output, the doc search filters and <c>read_document</c>.</summary>
public sealed class LinkedDocumentExposureTests
{
    private const string App = "sales-app";
    private const string Tenant = "tenant-a";

    private sealed class Harness
    {
        public InMemoryDocLinkRepository Links { get; } = new();
        public InMemoryDocumentRepository Docs { get; }
        public InMemoryDocSearchService Search { get; } = new();
        public FakeDiscoveryTableRepository Tables { get; } = new();
        public GovernanceDocStore Store { get; }
        public DocLinkDiscoveryReader Reader { get; }
        public DiscoveryService Discovery { get; }

        public Harness()
        {
            Docs = new InMemoryDocumentRepository { Links = Links };
            Store = new GovernanceDocStore(Docs, Search, new MarkdownDocIngestor(), NullLogger<GovernanceDocStore>.Instance);
            Tables.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", "orders", Tenant, columns: [("id", false, false), ("secret", true, true), ("amount", false, false)]));
            Tables.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", "hidden_tbl", Tenant, hidden: true));
            Tables.Tables.Add(DiscoveryTestBuilder.MakeTable("other", "foreign", "tenant-b"));
            var options = Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() });
            Reader = new DocLinkDiscoveryReader(Tables, Links, Docs, Store, options);
            Discovery = new DiscoveryService(
                Tables, FakeAccessDecisionPoint.AllowAll(), new FakeDiscoveryConsentRepository(), new FakeDiscoveryApprovalRepository(),
                new FakeDiscoveryOwnershipRepository(), new FakeDiscoveryArtifactStore(), new FakeAuditWriter(), options,
                Options.Create(new CatalogDiscoveryOptions()), logger: NullLogger<DiscoveryService>.Instance, docLinks: Reader);
        }

        public async Task SeedAsync(string? docTenant, string docId, string title = "Orders guide", string content = "# Orders\n\nHow orders flow.", bool hidden = false, int daysAgo = 0)
        {
            await Docs.CreateAsync(
                new DocRecord(docTenant, App, DocCategory.Runbook, docId, title, content, "sha", content.Length, [], null, "S-W", 1, DateTimeOffset.UtcNow.AddDays(-daysAgo), null, hidden),
                new DocQuota(1000, 100_000_000));
            await Store.RefreshIndexAsync();
        }

        public Task LinkAsync(string docId, string? docTenant, string domain, string name, string? column = null, string linkTenant = Tenant) =>
            Links.ReplaceForSourceAsync(
                linkTenant,
                DocLinkSource.ForDocument(new DocKey(docTenant, App, DocCategory.Runbook, docId)),
                [.. Links.Snapshot().Where(l => l.Source.DocId == docId && l.TenantId == linkTenant).Select(l => l.Target),
                    new DocLinkTarget(column is null ? DocLinkTargetKind.Table : DocLinkTargetKind.Column, domain, "dbo", name, column)],
                "S-W");

        public async Task<DiscoveryObject?> DescribeAsync(string domain, string name, string[]? roles = null)
        {
            var principal = DiscoveryTestBuilder.User("S-USER", Tenant, roles ?? ["AiAgent"]);
            return await Discovery.GetAsync(new TableIdentifier(domain, "dbo", name), DiscoveryTestBuilder.Caller(principal), default);
        }
    }

    private static McpSessionContext Session(bool admin = false, string tenant = Tenant) => new(
        "s1", "sp", tenant, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-USER", Roles: admin ? ["GovernanceAdmin"] : ["AiAgent"]);

    [Fact]
    public async Task DescribeDataset_ReturnsLinkedDocumentsApplicationsAndColumnDocuments()
    {
        var h = new Harness();
        await h.SeedAsync(Tenant, "orders-guide");
        await h.SeedAsync(null, "official-guide", title: "Official orders", daysAgo: 1);
        await h.SeedAsync(Tenant, "amount-note", title: "Amount note");
        await h.LinkAsync("orders-guide", Tenant, "sales", "orders");
        await h.LinkAsync("official-guide", null, "sales", "orders");
        await h.LinkAsync("amount-note", Tenant, "sales", "orders", "amount");
        await h.Links.ReplaceForSourceAsync(Tenant, DocLinkSource.ForApplication(App), [new DocLinkTarget(DocLinkTargetKind.Table, "sales", "dbo", "orders")], "S-W");

        var obj = await h.DescribeAsync("sales", "orders");

        obj.ShouldNotBeNull();
        obj.Documents.Select(d => d.DocId).ShouldBe(["orders-guide", "official-guide"]);
        obj.Documents[0].Trust.ShouldBe("untrusted");
        obj.Documents[1].Official.ShouldBeTrue();
        obj.Documents[1].Trust.ShouldBe("official");
        obj.Applications.Single().AppId.ShouldBe(App);
        obj.Columns.Single(c => c.Name == "amount").Documents.Single().DocId.ShouldBe("amount-note");
        obj.Columns.Single(c => c.Name == "id").Documents.ShouldBeEmpty();
        McpLinkedDocuments.From(obj).Documents!.Select(d => d.Trust).ShouldBe([DocTrustLevel.UntrustedUserContent, DocTrustLevel.Official]);
    }

    [Fact]
    public async Task RefsCapped_TruncatedFlagSet()
    {
        var h = new Harness();
        for (var i = 0; i < 12; i++)
        {
            await h.SeedAsync(Tenant, $"doc-{i:D2}", daysAgo: i);
            await h.LinkAsync($"doc-{i:D2}", Tenant, "sales", "orders");
        }

        var obj = await h.DescribeAsync("sales", "orders");

        obj!.Documents.Count.ShouldBe(10);
        obj.DocumentsTruncated.ShouldBeTrue();
        obj.DocumentCount.ShouldBe(12);
        obj.Documents[0].DocId.ShouldBe("doc-00");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task LinkedDocument_HiddenDocument_IsOmittedFromDiscovery()
    {
        var h = new Harness();
        await h.SeedAsync(Tenant, "visible");
        await h.SeedAsync(Tenant, "secret-doc", hidden: true);
        await h.LinkAsync("visible", Tenant, "sales", "orders");
        await h.LinkAsync("secret-doc", Tenant, "sales", "orders");

        var asUser = await h.DescribeAsync("sales", "orders");
        var asAdmin = await h.DescribeAsync("sales", "orders", ["GovernanceAdmin"]);

        asUser!.Documents.Select(d => d.DocId).ShouldBe(["visible"]);
        asAdmin!.Documents.Select(d => d.DocId).ShouldBe(["visible"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task LinkedDocument_ForeignTenantDocument_IsOmitted()
    {
        var h = new Harness();
        await h.SeedAsync("tenant-b", "foreign-doc");
        await h.LinkAsync("foreign-doc", "tenant-b", "sales", "orders");

        var obj = await h.DescribeAsync("sales", "orders");

        obj!.Documents.ShouldBeEmpty();
        obj.Applications.ShouldBeEmpty();
        (await h.Reader.DocumentsLinkedToAsync(Tenant, new TableIdentifier("sales", "dbo", "orders"), null))!.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task LinkedDocument_TargetNotDiscoverable_IsOmitted()
    {
        var h = new Harness();
        await h.SeedAsync(Tenant, "guide");
        await h.LinkAsync("guide", Tenant, "sales", "hidden_tbl");
        await h.LinkAsync("guide", Tenant, "other", "foreign", linkTenant: "tenant-b");

        var hidden = await h.DescribeAsync("sales", "hidden_tbl", ["GovernanceAdmin"]);

        hidden.ShouldNotBeNull();
        hidden.IsHidden.ShouldBeTrue();
        hidden.Documents.ShouldBeEmpty();
        (await h.Reader.DocumentsLinkedToAsync(Tenant, new TableIdentifier("sales", "dbo", "hidden_tbl"), null)).ShouldBeNull();
        (await h.Reader.DocumentsLinkedToAsync(Tenant, new TableIdentifier("other", "dbo", "foreign"), null)).ShouldBeNull();
        (await h.Reader.VisibleTargetsOfAsync(Tenant, new DocKey(Tenant, App, DocCategory.Runbook, "guide"))).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task LinkedDocument_HiddenColumnTarget_IsOmitted()
    {
        var h = new Harness();
        await h.SeedAsync(Tenant, "secret-col-doc", title: "About the secret column");
        await h.LinkAsync("secret-col-doc", Tenant, "sales", "orders", "secret");

        var obj = await h.DescribeAsync("sales", "orders");

        obj!.Documents.ShouldBeEmpty();
        obj.Columns.ShouldAllBe(c => c.Documents.Count == 0);
        (await h.Reader.DocumentsLinkedToAsync(Tenant, new TableIdentifier("sales", "dbo", "orders"), "secret")).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SearchDocs_HiddenDocument_NotReturnedToNonWriter()
    {
        var h = new Harness();
        await h.SeedAsync(Tenant, "shown", title: "Shown quarterly report", content: "# Shown\n\nquarterly figures");
        await h.SeedAsync(Tenant, "hid", title: "Hidden quarterly report", content: "# Hidden\n\nquarterly figures", hidden: true);
        await h.SeedAsync(null, "official-hid", title: "Hidden official quarterly", content: "# Official\n\nquarterly figures", hidden: true);

        var user = await McpDocTools.ExecuteSearchDocsAsync(h.Search, """{ "query": "quarterly" }""", Session());
        var tenantAdmin = await McpDocTools.ExecuteSearchDocsAsync(h.Search, """{ "query": "quarterly" }""", Session(admin: true));
        var topicsUser = await McpDocTools.ExecuteListDocTopicsAsync(h.Search, "{}", Session());
        var topicsAdmin = await McpDocTools.ExecuteListDocTopicsAsync(h.Search, "{}", Session(admin: true));

        user.ShouldContain("shown");
        user.ShouldNotContain("hid");
        user.ShouldNotContain("official-hid");
        tenantAdmin.ShouldContain("\"hid\"");
        tenantAdmin.ShouldNotContain("official-hid");
        topicsUser.ShouldNotContain("Hidden");
        topicsAdmin.ShouldContain("Hidden");
        (await h.Search.SearchAsync("quarterly", DocAccessScope.ForTenant(Tenant), null, 10)).Select(r => r.Id.DocId).ShouldBe(["shown"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SearchDocs_DatasetFilter_RestrictsToLinkedDocuments_AndHiddenDatasetReturnsEmpty()
    {
        var h = new Harness();
        await h.SeedAsync(Tenant, "linked", title: "Linked report", content: "# Linked\n\nreport text");
        await h.SeedAsync(Tenant, "unlinked", title: "Unlinked report", content: "# Unlinked\n\nreport text");
        await h.SeedAsync(Tenant, "on-hidden", title: "Hidden table report", content: "# H\n\nreport text");
        await h.LinkAsync("linked", Tenant, "sales", "orders");
        await h.LinkAsync("on-hidden", Tenant, "sales", "hidden_tbl");

        var filtered = JsonDocument.Parse(await McpDocTools.ExecuteSearchDocsAsync(h.Search, """{ "query": "report", "dataset": "sales.dbo.orders" }""", Session(), h.Reader));
        var onHidden = JsonDocument.Parse(await McpDocTools.ExecuteSearchDocsAsync(h.Search, """{ "query": "report", "dataset": "sales.dbo.hidden_tbl" }""", Session(), h.Reader));
        var unknown = JsonDocument.Parse(await McpDocTools.ExecuteSearchDocsAsync(h.Search, """{ "query": "report", "dataset": "sales.dbo.nope" }""", Session(), h.Reader));

        filtered.RootElement.GetProperty("total").GetInt32().ShouldBe(1);
        var hit = filtered.RootElement.GetProperty("results")[0];
        hit.GetProperty("id").GetProperty("docId").GetString().ShouldBe("linked");
        hit.GetProperty("linkedTargets")[0].GetString().ShouldBe("sales.dbo.orders");
        onHidden.RootElement.GetProperty("total").GetInt32().ShouldBe(0);
        unknown.RootElement.GetProperty("total").GetInt32().ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadDocument_HiddenOrForeign_SameNotFoundAsUnknown()
    {
        var h = new Harness();
        await h.SeedAsync(Tenant, "hid", hidden: true);
        await h.SeedAsync("tenant-b", "foreign");
        await h.SeedAsync(null, "official-hid", hidden: true);

        async Task<string> Read(string docId, McpSessionContext? s) =>
            await McpDocTools.ExecuteReadDocumentAsync(h.Store, $$"""{ "appId": "{{App}}", "category": "Runbook", "docId": "{{docId}}" }""", s);

        var unknown = await Read("nope", Session());
        (await Read("hid", Session())).ShouldBe(unknown);
        (await Read("foreign", Session())).ShouldBe(unknown);
        (await Read("official-hid", Session())).ShouldBe(unknown);
        (await Read("hid", null)).ShouldBe(unknown);
        (await Read("hid", Session(admin: true))).ShouldContain("\"docId\": \"hid\"");
        unknown.ShouldContain("NOT_FOUND");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadDocument_WrapsUntrustedMarkdown()
    {
        var h = new Harness();
        await h.SeedAsync(Tenant, "tenant-doc", title: "Tenant​ title", content: "# Guide\n\nHello‮ world <script>alert(1)</script>");
        await h.SeedAsync(null, "official-doc", content: "# Official\n\nFine.");

        var tenantJson = JsonDocument.Parse(await McpDocTools.ExecuteReadDocumentAsync(h.Store, $$"""{ "appId": "{{App}}", "category": "Runbook", "docId": "tenant-doc" }""", Session())).RootElement;
        var officialJson = JsonDocument.Parse(await McpDocTools.ExecuteReadDocumentAsync(h.Store, $$"""{ "appId": "{{App}}", "category": "Runbook", "docId": "official-doc" }""", Session())).RootElement;

        tenantJson.GetProperty("trust").GetString().ShouldBe(DocTrustLevel.UntrustedUserContent);
        tenantJson.GetProperty("contentNotice").GetString().ShouldBe(McpDocTools.UntrustedContentNotice);
        var content = tenantJson.GetProperty("content").GetString()!;
        content.ShouldNotContain("‮");
        content.ShouldNotContain("<script>");
        tenantJson.GetProperty("title").GetString()!.ShouldNotContain("​");
        officialJson.GetProperty("trust").GetString().ShouldBe(DocTrustLevel.Official);
        officialJson.TryGetProperty("contentNotice", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ReadDocument_LongDocument_IsWindowedWithNextOffset()
    {
        var h = new Harness();
        var content = "# Big\n\n" + string.Concat(Enumerable.Repeat("0123456789abcdef\n", 6000));
        await h.SeedAsync(null, "big", content: content);

        var first = JsonDocument.Parse(await McpDocTools.ExecuteReadDocumentAsync(h.Store, $$"""{ "appId": "{{App}}", "category": "Runbook", "docId": "big" }""", Session())).RootElement;
        first.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        var next = first.GetProperty("nextOffset").GetInt32();
        next.ShouldBeGreaterThan(0);

        var second = JsonDocument.Parse(await McpDocTools.ExecuteReadDocumentAsync(h.Store, $$"""{ "appId": "{{App}}", "category": "Runbook", "docId": "big", "offset": {{next}} }""", Session())).RootElement;
        (first.GetProperty("content").GetString()!.Length + second.GetProperty("content").GetString()!.Length).ShouldBeGreaterThan(next);
        second.GetProperty("offset").GetInt32().ShouldBe(next);
    }

    [Fact]
    public void ReadDocument_IsRegisteredAsDocToolWithDefinition()
    {
        McpDocTools.IsDocTool(McpDocTools.ReadDocument).ShouldBeTrue();
        McpDocTools.Definitions().Select(d => d.Name).ShouldContain(McpDocTools.ReadDocument);
    }
}
