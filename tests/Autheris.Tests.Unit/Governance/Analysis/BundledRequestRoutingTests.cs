namespace Autheris.Tests.Unit.Governance.Analysis;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Analysis;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.Catalog;
using Autheris.Tests.Unit.TestSupport;
using Shouldly;
using Xunit;

/// <summary>A bundle is only a list of items: every item is handed to the engine as its own request, routed and decided on its own.</summary>
public sealed class BundledRequestRoutingTests
{
    private static BundledConsentRequestService Service(RecordingPermissionRequestEngine engine, int tableCount = 3)
    {
        var tables = Enumerable.Range(1, tableCount)
            .Select(i => Wp0.Table("sales", $"t{i}", $"T{i}", ["id", "secret_col"]))
            .ToArray();
        return new BundledConsentRequestService(Wp0.Tables(tables), Wp0.Consents(), engine: engine);
    }

    private static RequestableConsentItem Item(int i, string? column = null) =>
        new($"sales.dbo.t{i}", column, "READ", "Analytics need");

    [Fact]
    public async Task Each_item_is_one_request_with_its_own_kind_and_target()
    {
        var engine = new RecordingPermissionRequestEngine();
        var result = await Service(engine).SubmitBundledRequestAsync(
            Wp0.User("S-REQ", "tenant-a"),
            new BundledConsentRequest([Item(1), Item(2, "secret_col"), Item(3)], "Analytical business reporting", "PR-1"));

        var bundle = engine.Bundles.ShouldHaveSingleItem();
        bundle.Count.ShouldBe(3);
        bundle.Count(s => s.RequestKind == ConsentRequestKind.Data).ShouldBe(2);
        var unmask = bundle.Single(s => s.RequestKind == ConsentRequestKind.Unmask);
        unmask.Target.Columns.ShouldBe(["secret_col"]);
        bundle.ShouldAllBe(s => s.GranteeRef == "S-REQ" && s.Justification!.Contains("[Ref:PR-1]"));
        result.Tickets.SelectMany(t => t.UnderlyingConsentRequestIds).Distinct().Count().ShouldBe(3);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Bundle_cap_enforced()
    {
        var engine = new RecordingPermissionRequestEngine(maxBundleItems: 2);
        var service = Service(engine);

        var ex = await Should.ThrowAsync<ConsentRequestException>(() => service.SubmitBundledRequestAsync(
            Wp0.User("S-REQ", "tenant-a"),
            new BundledConsentRequest([Item(1), Item(2), Item(3)], "Analytical business reporting")));

        ex.Code.ShouldBe("BUNDLE_LIMIT_EXCEEDED");
        engine.Bundles.ShouldBeEmpty();
        engine.Submissions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Hidden_table_refuses_the_whole_bundle_before_any_submission()
    {
        var visible = Wp0.Table("sales", "t1", "T1", ["id"]);
        var hidden = Wp0.Table("sales", "t2", "T2", ["id"]);
        hidden = hidden with { Table = hidden.Table with { DiscoveryHidden = true } };
        var engine = new RecordingPermissionRequestEngine();
        var service = new BundledConsentRequestService(Wp0.Tables(visible, hidden), Wp0.Consents(), engine: engine);

        await Should.ThrowAsync<System.Collections.Generic.KeyNotFoundException>(() => service.SubmitBundledRequestAsync(
            Wp0.User("S-REQ", "tenant-a"),
            new BundledConsentRequest([Item(1), Item(2)], "Analytical business reporting")));

        engine.Submissions.ShouldBeEmpty();
    }
}
