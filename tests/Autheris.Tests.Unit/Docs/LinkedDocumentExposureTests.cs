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
using Autheris.Domain.Interfaces;
using Autheris.Application.Policy.Pdp;
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
        public DocLinkCallerView CallerView { get; }
        public ThrowingCallerView? Failing { get; set; }
        public DiscoveryService Discovery { get; }

        public Harness(bool denyAmount = false)
        {
            Docs = new InMemoryDocumentRepository { Links = Links };
            Store = new GovernanceDocStore(Docs, Search, new MarkdownDocIngestor(), NullLogger<GovernanceDocStore>.Instance);
            Tables.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", "orders", Tenant, columns: [("id", false, false), ("secret", true, true), ("amount", false, false)]));
            Tables.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", "hidden_tbl", Tenant, hidden: true));
            Tables.Tables.Add(DiscoveryTestBuilder.MakeTable("other", "foreign", "tenant-b"));
            var options = Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() });
            Reader = new DocLinkDiscoveryReader(Links, Docs, Store);
            Discovery = new DiscoveryService(
                Tables, denyAmount ? DenyAmountColumn() : FakeAccessDecisionPoint.AllowAll(), new FakeDiscoveryConsentRepository(), new FakeDiscoveryApprovalRepository(),
                new FakeDiscoveryOwnershipRepository(), new FakeDiscoveryArtifactStore(), new FakeAuditWriter(), options,
                Options.Create(new CatalogDiscoveryOptions()), logger: NullLogger<DiscoveryService>.Instance, docLinks: Reader);
            CallerView = new DocLinkCallerView(Discovery, Reader);
        }

        private static FakeAccessDecisionPoint DenyAmountColumn() => new(r => TableAccessDecision.Allowed(
            r.Object.Identifier,
            r.Object.Columns.ToDictionary(c => c.ColumnName, c => c.ColumnName == "amount" ? ColumnAccessLevel.Deny : ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase)));

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

    private sealed class ThrowingCallerView : IDocLinkCallerView
    {
        public Task<IReadOnlySet<DocKey>?> DocumentsLinkedToAsync(System.Security.Claims.ClaimsPrincipal principal, string tenantId, TableIdentifier table, string? column, System.Threading.CancellationToken ct = default) =>
            throw new InvalidOperationException("link store down");

        public Task<IReadOnlyDictionary<DocKey, IReadOnlyList<string>>> VisibleTargetsOfAsync(System.Security.Claims.ClaimsPrincipal principal, string tenantId, IReadOnlyList<DocKey> keys, System.Threading.CancellationToken ct = default) =>
            throw new InvalidOperationException("link store down");
    }

    private static System.Security.Claims.ClaimsPrincipal Principal() => DiscoveryTestBuilder.User("S-USER", Tenant, ["AiAgent"]);

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
        (await h.CallerView.DocumentsLinkedToAsync(Principal(), Tenant, new TableIdentifier("sales", "dbo", "orders"), null))!.ShouldBeEmpty();
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
        (await h.CallerView.DocumentsLinkedToAsync(Principal(), Tenant, new TableIdentifier("sales", "dbo", "hidden_tbl"), null)).ShouldBeNull();
        (await h.CallerView.DocumentsLinkedToAsync(Principal(), Tenant, new TableIdentifier("other", "dbo", "foreign"), null)).ShouldBeNull();
        (await h.CallerView.VisibleTargetsOfAsync(Principal(), Tenant, [new DocKey(Tenant, App, DocCategory.Runbook, "guide")])).ShouldBeEmpty();
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
        (await h.CallerView.DocumentsLinkedToAsync(Principal(), Tenant, new TableIdentifier("sales", "dbo", "orders"), "secret")).ShouldBeNull();
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

        var filtered = JsonDocument.Parse(await McpDocTools.ExecuteSearchDocsAsync(h.Search, """{ "query": "report", "dataset": "sales.dbo.orders" }""", Session(), h.CallerView));
        var onHidden = JsonDocument.Parse(await McpDocTools.ExecuteSearchDocsAsync(h.Search, """{ "query": "report", "dataset": "sales.dbo.hidden_tbl" }""", Session(), h.CallerView));
        var unknown = JsonDocument.Parse(await McpDocTools.ExecuteSearchDocsAsync(h.Search, """{ "query": "report", "dataset": "sales.dbo.nope" }""", Session(), h.CallerView));

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

    [Fact]
    [Trait("Category", "Security")]
    public async Task SearchDocs_ColumnFilter_DeniedColumn_ReturnsEmpty()
    {
        var h = new Harness(denyAmount: true);
        await h.SeedAsync(Tenant, "amount-note", title: "Amount note", content: "# Amount\n\nreport text");
        await h.LinkAsync("amount-note", Tenant, "sales", "orders", "amount");

        var denied = JsonDocument.Parse(await McpDocTools.ExecuteSearchDocsAsync(h.Search, """{ "query": "report", "dataset": "sales.dbo.orders", "column": "amount" }""", Session(), h.CallerView));

        denied.RootElement.GetProperty("total").GetInt32().ShouldBe(0);
        (await h.CallerView.DocumentsLinkedToAsync(Principal(), Tenant, new TableIdentifier("sales", "dbo", "orders"), "amount")).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SearchDocs_LinkedTargets_OmitDeniedColumn()
    {
        var h = new Harness(denyAmount: true);
        await h.SeedAsync(Tenant, "note", title: "Note", content: "# Note\n\nreport text");
        await h.LinkAsync("note", Tenant, "sales", "orders", "amount");
        await h.LinkAsync("note", Tenant, "sales", "orders", "id");

        var json = await McpDocTools.ExecuteSearchDocsAsync(h.Search, """{ "query": "report" }""", Session(), h.CallerView);

        json.ShouldContain("sales.dbo.orders.id");
        json.ShouldNotContain("amount");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Discovery_DeniedColumn_HasNoLinkedDocuments()
    {
        var h = new Harness(denyAmount: true);
        await h.SeedAsync(Tenant, "note", title: "Note");
        await h.SeedAsync(Tenant, "table-note", title: "Table note");
        await h.LinkAsync("note", Tenant, "sales", "orders", "amount");
        await h.LinkAsync("table-note", Tenant, "sales", "orders");

        var obj = await h.DescribeAsync("sales", "orders");

        obj!.Columns.Single(c => c.Name == "amount").State.ShouldBe(DiscoveryColumnState.NoAccess);
        obj.Columns.ShouldAllBe(c => c.Documents.Count == 0);
        McpLinkedDocuments.From(obj).ColumnDocuments.ShouldBeEmpty();
        obj.Documents.Select(d => d.DocId).ShouldBe(["table-note"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SearchDocs_ReaderFailure_ReturnsNoDocuments_AndNoError()
    {
        var h = new Harness();
        await h.SeedAsync(Tenant, "linked", title: "Linked report", content: "# Linked\n\nreport text");
        await h.LinkAsync("linked", Tenant, "sales", "orders");
        var failing = new ThrowingCallerView();

        var filtered = JsonDocument.Parse(await McpDocTools.ExecuteSearchDocsAsync(h.Search, """{ "query": "report", "dataset": "sales.dbo.orders" }""", Session(), failing));
        var plain = JsonDocument.Parse(await McpDocTools.ExecuteSearchDocsAsync(h.Search, """{ "query": "report" }""", Session(), failing));

        filtered.RootElement.GetProperty("total").GetInt32().ShouldBe(0);
        plain.RootElement.GetProperty("total").GetInt32().ShouldBe(1);
        plain.RootElement.GetProperty("results")[0].TryGetProperty("linkedTargets", out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Discovery_DocumentCountAndTruncation_IgnoreHiddenAndForeignDocuments()
    {
        var h = new Harness();
        for (var i = 0; i < 10; i++)
        {
            await h.SeedAsync(Tenant, $"doc-{i:D2}", daysAgo: i);
            await h.LinkAsync($"doc-{i:D2}", Tenant, "sales", "orders");
        }

        var before = await h.DescribeAsync("sales", "orders");
        await h.SeedAsync(Tenant, "hidden-one", hidden: true);
        await h.SeedAsync("tenant-b", "foreign-one");
        await h.LinkAsync("hidden-one", Tenant, "sales", "orders");
        await h.LinkAsync("foreign-one", "tenant-b", "sales", "orders");
        var after = await h.DescribeAsync("sales", "orders");

        after!.DocumentCount.ShouldBe(before!.DocumentCount);
        after.DocumentsTruncated.ShouldBe(before.DocumentsTruncated);
        after.Documents.Count.ShouldBe(before.Documents.Count);
        McpLinkedDocuments.From(after).Truncated.ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task HiddenDocuments_AbsentFromErrorCodeProcedureAndApplicationCounts_ButVisibleToWriter()
    {
        var h = new Harness();
        await h.SeedAsync(Tenant, "visible", content: "# Visible\n\ntext");
        await h.Docs.CreateAsync(
            new DocRecord(Tenant, App, DocCategory.ErrorCode, "err-hid", "Err hidden", "# ERR_HIDDEN_42\n\nSymptom text", "sha", 20, [], "ERR_HIDDEN_42", "S-W", 1, DateTimeOffset.UtcNow, null, true),
            new DocQuota(1000, 100_000_000));
        await h.SeedAsync(Tenant, "hid", content: "# Hidden\n\nsecret text", hidden: true);

        var lookupUser = await McpDocTools.ExecuteLookupErrorCodeAsync(h.Search, """{ "errorCode": "ERR_HIDDEN_42" }""", Session());
        var lookupAdmin = await McpDocTools.ExecuteLookupErrorCodeAsync(h.Search, """{ "errorCode": "ERR_HIDDEN_42" }""", Session(admin: true));
        var procUser = await McpDocTools.ExecuteGetProcedureAsync(h.Search, $$"""{ "identifier": "{{App}}::Runbook::hid#hidden" }""", Session());
        var procAdmin = await McpDocTools.ExecuteGetProcedureAsync(h.Search, $$"""{ "identifier": "{{App}}::Runbook::hid#hidden" }""", Session(admin: true));
        var appsUser = JsonDocument.Parse(await McpDocTools.ExecuteListApplicationsAsync(h.Store, "{}", Session())).RootElement.ToString();
        var appsAdmin = JsonDocument.Parse(await McpDocTools.ExecuteListApplicationsAsync(h.Store, "{}", Session(admin: true))).RootElement.ToString();

        lookupUser.ShouldNotContain("ERR_HIDDEN_42");
        lookupAdmin.ShouldContain("ERR_HIDDEN_42");
        procUser.ShouldContain("NOT_FOUND");
        procAdmin.ShouldContain("secret text");
        appsUser.ShouldContain("\"documentCount\": 1");
        appsAdmin.ShouldContain("\"documentCount\": 3");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task HiddenDocuments_ReadOnlyAdminSession_AndOtherTenantAdmin_NeverSeeThem()
    {
        var h = new Harness();
        await h.SeedAsync(Tenant, "hid", title: "Hidden quarterly", content: "# Hidden\n\nquarterly text", hidden: true);
        await h.SeedAsync(null, "official-hid", title: "Official quarterly", content: "# Official\n\nquarterly text", hidden: true);
        var readOnlyAdmin = Session(admin: true) with { IsReadOnly = true };
        var otherTenantAdmin = Session(admin: true, tenant: "tenant-b");

        foreach (var session in new[] { readOnlyAdmin, otherTenantAdmin })
        {
            (await McpDocTools.ExecuteSearchDocsAsync(h.Search, """{ "query": "quarterly" }""", session)).ShouldContain("\"total\": 0");
            (await McpDocTools.ExecuteReadDocumentAsync(h.Store, $$"""{ "appId": "{{App}}", "category": "Runbook", "docId": "hid" }""", session)).ShouldContain("NOT_FOUND");
            (await McpDocTools.ExecuteReadDocumentAsync(h.Store, $$"""{ "appId": "{{App}}", "category": "Runbook", "docId": "official-hid" }""", session)).ShouldContain("NOT_FOUND");
        }

        DocAccessPolicy.ResolveReader(new DocCaller(false, true, Tenant, "S", true, true)).HiddenTenantVisible.ShouldBeFalse();
        DocAccessPolicy.ResolveReader(new DocCaller(true, false, Tenant, "S", true, true)).HiddenOfficialVisible.ShouldBeFalse();
        DocAccessPolicy.ResolveReader(new DocCaller(false, true, "tenant-b", "S", true, false)).CanSee(
            (await h.Search.SnapshotVisibleChunksAsync(DocAccessScope.ForTenant(Tenant, false, true))).First(c => c.TenantId == Tenant)).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RestScopes_HiddenDocuments_AbsentFromSearchListAndGet_ForNonWriter()
    {
        var h = new Harness();
        await h.SeedAsync(Tenant, "shown", content: "# Shown\n\nquarterly text");
        await h.SeedAsync(Tenant, "hid", content: "# Hidden\n\nquarterly text", hidden: true);
        var nonWriter = DocAccessScope.ForTenant(Tenant);
        var writer = DocAccessScope.ForTenant(Tenant, hiddenOfficialVisible: false, hiddenTenantVisible: true);
        var hiddenId = new DocChunkIdentifier(App, "Runbook", "hid", "hidden");

        (await h.Search.SearchAsync("quarterly", nonWriter, null, 10)).Select(r => r.Id.DocId).ShouldBe(["shown"]);
        (await h.Search.SnapshotVisibleChunksAsync(nonWriter)).ShouldAllBe(c => c.Id.DocId != "hid");
        (await h.Search.GetChunkAsync(hiddenId, nonWriter)).ShouldBeNull();
        (await h.Search.ListTopicsAsync(nonWriter)).ShouldAllBe(t => !t.Contains("Hidden", StringComparison.Ordinal));
        (await h.Search.GetChunkAsync(hiddenId, writer)).ShouldNotBeNull();
        (await h.Search.SearchAsync("quarterly", writer, null, 10)).Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ReadDocument_HugeOffset_ReturnsEmptyWindow_AndContinuationWindowIsSanitised()
    {
        var h = new Harness();
        var content = "# Big\n\n" + string.Concat(Enumerable.Repeat("0123456789abcdef\n", 6000)) + "tail\u202E<script>x</script>";
        await h.SeedAsync(Tenant, "big", content: content);

        var huge = JsonDocument.Parse(await McpDocTools.ExecuteReadDocumentAsync(h.Store, $$"""{ "appId": "{{App}}", "category": "Runbook", "docId": "big", "offset": 2147483647 }""", Session())).RootElement;
        var first = JsonDocument.Parse(await McpDocTools.ExecuteReadDocumentAsync(h.Store, $$"""{ "appId": "{{App}}", "category": "Runbook", "docId": "big" }""", Session())).RootElement;
        var next = first.GetProperty("nextOffset").GetInt32();
        var second = JsonDocument.Parse(await McpDocTools.ExecuteReadDocumentAsync(h.Store, $$"""{ "appId": "{{App}}", "category": "Runbook", "docId": "big", "offset": {{next}} }""", Session())).RootElement;

        huge.GetProperty("content").GetString().ShouldBe(string.Empty);
        huge.TryGetProperty("truncated", out _).ShouldBeFalse();
        var tail = second.GetProperty("content").GetString()!;
        tail.ShouldContain("tail");
        tail.ShouldNotContain("\u202E");
        tail.ShouldNotContain("<script>");
        second.GetProperty("contentNotice").GetString().ShouldBe(McpDocTools.UntrustedContentNotice);
    }
}
