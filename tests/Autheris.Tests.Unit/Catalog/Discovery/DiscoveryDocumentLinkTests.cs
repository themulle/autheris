namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Docs.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Docs;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class DiscoveryDocumentLinkTests
{
    private const string App = "sales-app";
    private const string DocId = "orders-guide";

    private sealed record Harness(
        DocLinkService Service, InMemoryDocLinkRepository Links, InMemoryDocumentRepository Docs,
        FakeDiscoveryTableRepository Tables, FakeDiscoveryOwnershipRepository Owners, FakeAuditWriter Audit);

    private static Harness Create(bool hiddenDoc = false, string docTenant = "tenant-a")
    {
        var tables = new FakeDiscoveryTableRepository();
        tables.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", "orders", "tenant-a", columns: [("id", false, false), ("secret", true, true)]));
        tables.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", "invoices", "tenant-a"));
        tables.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", "hidden_tbl", "tenant-a", hidden: true));
        tables.Tables.Add(DiscoveryTestBuilder.MakeTable("other", "foreign", "tenant-b"));
        var links = new InMemoryDocLinkRepository();
        var docs = new InMemoryDocumentRepository { Links = links };
        docs.CreateAsync(new DocRecord(docTenant, App, DocCategory.Runbook, DocId, "Orders guide", "# Orders", "sha", 8, [], null, "S-W", 1, DateTimeOffset.UtcNow, null, hiddenDoc),
            new DocQuota(100, 1_000_000)).GetAwaiter().GetResult();
        var owners = new FakeDiscoveryOwnershipRepository();
        var audit = new FakeAuditWriter();
        var service = new DocLinkService(tables, owners, links, docs, audit, Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }));
        return new Harness(service, links, docs, tables, owners, audit);
    }

    private static ClaimsPrincipal Admin(string tenant = "tenant-a") => DiscoveryTestBuilder.User("S-ADMIN", tenant, ["GovernanceAdmin"]);

    private static DocCaller AdminCaller(string tenant = "tenant-a") => new(false, true, tenant, "S-ADMIN", true, false);

    private static ClaimsPrincipal Plain(string sid = "S-USER") => DiscoveryTestBuilder.User(sid, "tenant-a", ["AiAgent"]);

    private static DocCaller PlainCaller(string sid = "S-USER") => new(false, false, "tenant-a", sid, true, false);

    private static DocLinkTarget Table(string domain, string name) => new(DocLinkTargetKind.Table, domain, "dbo", name);

    [Fact]
    public async Task Writer_ReplacesLinks_AndRelinkingIsIdempotent()
    {
        var h = Create();
        var first = await h.Service.ReplaceDocumentLinksAsync(Admin(), AdminCaller(), App, DocCategory.Runbook, DocId, [Table("sales", "orders"), new DocLinkTarget(DocLinkTargetKind.Column, "sales", "dbo", "orders", "id")]);
        first.Outcome.ShouldBe(DocLinkOutcome.Ok);
        h.Links.Snapshot().Count.ShouldBe(2);
        var second = await h.Service.ReplaceDocumentLinksAsync(Admin(), AdminCaller(), App, DocCategory.Runbook, DocId, [Table("sales", "invoices")]);
        second.Outcome.ShouldBe(DocLinkOutcome.Ok);
        h.Links.Snapshot().Select(l => l.Target.Name).ShouldBe(["invoices"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Link_ToNonDiscoverableTarget_404()
    {
        var h = Create();
        var unknown = await h.Service.ReplaceDocumentLinksAsync(Admin(), AdminCaller(), App, DocCategory.Runbook, DocId, [Table("sales", "does_not_exist")]);
        var foreign = await h.Service.ReplaceDocumentLinksAsync(Admin(), AdminCaller(), App, DocCategory.Runbook, DocId, [Table("other", "foreign")]);
        var hiddenCol = await h.Service.ReplaceDocumentLinksAsync(Plain(), PlainCaller(), App, DocCategory.Runbook, DocId, [new DocLinkTarget(DocLinkTargetKind.Column, "sales", "dbo", "orders", "secret")]);
        var procedure = await h.Service.ReplaceDocumentLinksAsync(Admin(), AdminCaller(), App, DocCategory.Runbook, DocId, [new DocLinkTarget(DocLinkTargetKind.Procedure, "sales", "dbo", "p")]);
        foreach (var r in new[] { unknown, foreign, hiddenCol, procedure }) r.Outcome.ShouldBe(DocLinkOutcome.NotFound);
        h.Links.Snapshot().ShouldBeEmpty();
        h.Audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Link_ToHiddenTable_ByNonOwner_404_ButAdminMayLink()
    {
        var h = Create();
        (await h.Service.ReplaceDocumentLinksAsync(Plain(), PlainCaller(), App, DocCategory.Runbook, DocId, [Table("sales", "hidden_tbl")])).Outcome.ShouldBe(DocLinkOutcome.NotFound);
        (await h.Service.ReplaceDocumentLinksAsync(Admin(), AdminCaller(), App, DocCategory.Runbook, DocId, [Table("sales", "hidden_tbl")])).Outcome.ShouldBe(DocLinkOutcome.Ok);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Link_ByNonWriterNonOwner_403()
    {
        var h = Create();
        var result = await h.Service.ReplaceDocumentLinksAsync(Plain(), PlainCaller(), App, DocCategory.Runbook, DocId, [Table("sales", "orders")]);
        result.Outcome.ShouldBe(DocLinkOutcome.Forbidden);
        h.Links.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Link_ByTargetOwner_KeepsLinksToTablesTheOwnerDoesNotManage()
    {
        var h = Create();
        await h.Service.ReplaceDocumentLinksAsync(Admin(), AdminCaller(), App, DocCategory.Runbook, DocId, [Table("sales", "orders"), Table("sales", "invoices")]);
        h.Owners.OwnedTables.Add(new Autheris.Domain.Common.TableIdentifier("sales", "dbo", "orders"));
        var owner = await h.Service.ReplaceDocumentLinksAsync(Plain("S-OWNER"), PlainCaller("S-OWNER"), App, DocCategory.Runbook, DocId, []);
        owner.Outcome.ShouldBe(DocLinkOutcome.Forbidden);
        var ok = await h.Service.ReplaceDocumentLinksAsync(Plain("S-OWNER"), PlainCaller("S-OWNER"), App, DocCategory.Runbook, DocId, [Table("sales", "orders")]);
        ok.Outcome.ShouldBe(DocLinkOutcome.Ok);
        h.Links.Snapshot().Select(l => l.Target.Name).OrderBy(n => n).ShouldBe(["invoices", "orders"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task OtherTenantDocument_NeverLinked()
    {
        var h = Create(docTenant: "tenant-b");
        var result = await h.Service.ReplaceDocumentLinksAsync(Admin(), AdminCaller(), App, DocCategory.Runbook, DocId, [Table("sales", "orders")]);
        result.Outcome.ShouldBe(DocLinkOutcome.NotFound);
        h.Links.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task HiddenDocument_ForNonWriter_IdenticalToMissing()
    {
        var h = Create(hiddenDoc: true);
        h.Owners.OwnedTables.Add(new Autheris.Domain.Common.TableIdentifier("sales", "dbo", "orders"));
        var hidden = await h.Service.ReplaceDocumentLinksAsync(Plain(), PlainCaller(), App, DocCategory.Runbook, DocId, [Table("sales", "orders")]);
        var missing = await h.Service.ReplaceDocumentLinksAsync(Plain(), PlainCaller(), App, DocCategory.Runbook, "no-such-doc", [Table("sales", "orders")]);
        hidden.ShouldBe(missing);
        (await h.Service.SetDocumentHiddenAsync(Plain(), PlainCaller(), App, DocCategory.Runbook, DocId, false)).ShouldBe(missing);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Link_MoreThan50Targets_Rejected()
    {
        var h = Create();
        var many = Enumerable.Range(0, 51).Select(i => Table("sales", $"t{i}")).ToList();
        var result = await h.Service.ReplaceDocumentLinksAsync(Admin(), AdminCaller(), App, DocCategory.Runbook, DocId, many);
        result.Outcome.ShouldBe(DocLinkOutcome.InvalidRequest);
        h.Links.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task LinkChange_AuditedFailClosed_WithoutContent()
    {
        var h = Create();
        await h.Service.ReplaceDocumentLinksAsync(Admin(), AdminCaller(), App, DocCategory.Runbook, DocId, [Table("sales", "orders")]);
        var entry = h.Audit.Entries.Single();
        entry.EventType.ShouldBe(AuditEventTypes.DocLinksChanged);
        entry.DetailsJson.ShouldContain("Table:sales.dbo.orders");
        entry.DetailsJson.ShouldNotContain("# Orders");
        entry.DetailsJson.ShouldNotContain("Orders guide");

        h.Audit.FailWithUnavailable = true;
        var failed = await h.Service.ReplaceDocumentLinksAsync(Admin(), AdminCaller(), App, DocCategory.Runbook, DocId, [Table("sales", "invoices")]);
        failed.Outcome.ShouldBe(DocLinkOutcome.Unavailable);
        h.Links.Snapshot().Select(l => l.Target.Name).ShouldBe(["orders"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VisibilityChange_AuditedFailClosed_AndRolledBackWhenAuditFails()
    {
        var h = Create();
        (await h.Service.SetDocumentHiddenAsync(Admin(), AdminCaller(), App, DocCategory.Runbook, DocId, true)).Outcome.ShouldBe(DocLinkOutcome.Ok);
        (await h.Docs.GetAsync(new DocKey("tenant-a", App, DocCategory.Runbook, DocId)))!.DiscoveryHidden.ShouldBeTrue();
        var entry = h.Audit.Entries.Single();
        entry.EventType.ShouldBe(AuditEventTypes.DocVisibilityChanged);
        entry.DetailsJson.ShouldNotContain("# Orders");

        h.Audit.FailWithUnavailable = true;
        (await h.Service.SetDocumentHiddenAsync(Admin(), AdminCaller(), App, DocCategory.Runbook, DocId, false)).Outcome.ShouldBe(DocLinkOutcome.Unavailable);
        (await h.Docs.GetAsync(new DocKey("tenant-a", App, DocCategory.Runbook, DocId)))!.DiscoveryHidden.ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Hide_ByNonWriterWithoutOwnedLinks_403()
    {
        var h = Create();
        (await h.Service.SetDocumentHiddenAsync(Plain(), PlainCaller(), App, DocCategory.Runbook, DocId, true)).Outcome.ShouldBe(DocLinkOutcome.Forbidden);
    }

    [Fact]
    public async Task DocumentDelete_RemovesLinks()
    {
        var h = Create();
        await h.Service.ReplaceDocumentLinksAsync(Admin(), AdminCaller(), App, DocCategory.Runbook, DocId, [Table("sales", "orders")]);
        h.Links.Snapshot().Count.ShouldBe(1);
        await h.Docs.DeleteAsync(new DocKey("tenant-a", App, DocCategory.Runbook, DocId), null, DateTimeOffset.UtcNow);
        h.Links.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task ApplicationLinks_RequireApplicationWriter()
    {
        var h = Create();
        (await h.Service.ReplaceApplicationLinksAsync(Admin(), AdminCaller(), App, [Table("sales", "orders")])).Outcome.ShouldBe(DocLinkOutcome.Ok);
        h.Links.Snapshot().Single().Source.Kind.ShouldBe(DocLinkSourceKind.Application);
        (await h.Service.ReplaceApplicationLinksAsync(Plain(), PlainCaller(), App, [Table("sales", "orders")])).Outcome.ShouldBe(DocLinkOutcome.Forbidden);
    }
}
