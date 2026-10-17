namespace Autheris.Tests.Unit.Governance;

using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Governance.Analysis;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.Catalog;
using Shouldly;
using Xunit;

/// <summary>
/// WP-0 (PLAN-GOV-111 A-0.5, G-10): query analysis stops at table level for a table the caller has no grant, ownership or administrator class on,
/// whatever the catalog visibility value is. No <c>select *</c> expansion, no <c>OutputColumns</c>, no <c>UnknownColumn</c> existence oracle.
/// Latent hardening: every table is <c>Hidden</c> today, so this cannot become a leak when tiers are persisted.
/// </summary>
[Trait("Category", "Security")]
public sealed class QueryAnalysisUnconsentedTableTests
{
    private static TableMetadata Orders(DatasetVisibility visibility)
    {
        var t = Wp0.Table("sales", "orders", "Orders", ["id", "salary"]);
        return t with { Table = t.Table with { Visibility = visibility } };
    }

    private static Task<QueryAnalysisResult> AnalyzeAsync(DatasetVisibility visibility, string sql)
    {
        var service = new QueryAnalysisService(Wp0.Tables(Orders(visibility)), Wp0.Consents());
        return service.AnalyzeQueriesAsync([QueryAnalysisItem.FromSql(sql, name: "q")], Wp0.User("S-NOBODY", "tenant-a"));
    }

    [Theory]
    [InlineData(DatasetVisibility.Summary)]
    [InlineData(DatasetVisibility.Full)]
    public async Task SelectStar_WithoutGrant_ExpandsNoColumns_AndRequestsOnlyTheTable(DatasetVisibility visibility)
    {
        var result = await AnalyzeAsync(visibility, "SELECT * FROM sales.dbo.orders");

        result.ValidationReport.OutputColumns.ShouldBeEmpty();
        result.ValidationReport.Diagnostics.ShouldNotContain(d => d.Code == QueryDiagnosticCodes.UnknownColumn);
        var missing = result.AccessRequirements.MissingConsents;
        missing.ShouldHaveSingleItem().Column.ShouldBeNull();
        missing[0].RequestedLevel.ShouldBe("READ");
    }

    [Theory]
    [InlineData(DatasetVisibility.Summary)]
    [InlineData(DatasetVisibility.Full)]
    public async Task ExistingAndGhostColumns_AnswerAlike_NoExistenceOracle(DatasetVisibility visibility)
    {
        var real = await AnalyzeAsync(visibility, "SELECT salary FROM sales.dbo.orders WHERE id = 1");
        var ghost = await AnalyzeAsync(visibility, "SELECT ghost_column FROM sales.dbo.orders WHERE ghost_filter = 1");

        foreach (var result in new[] { real, ghost })
        {
            result.ValidationReport.OutputColumns.ShouldBeEmpty();
            result.ValidationReport.Diagnostics.ShouldNotContain(d => d.Code == QueryDiagnosticCodes.UnknownColumn);
            result.AccessRequirements.MissingConsents.ShouldHaveSingleItem().Column.ShouldBeNull();
        }

        real.AccessRequirements.MissingConsents.Select(m => (m.Dataset, m.RequestedLevel))
            .ShouldBe(ghost.AccessRequirements.MissingConsents.Select(m => (m.Dataset, m.RequestedLevel)));
    }

    [Fact]
    public async Task HiddenTable_WithoutGrant_StaysTheUnknownTableDiagnostic()
    {
        var result = await AnalyzeAsync(DatasetVisibility.Hidden, "SELECT * FROM sales.dbo.orders");

        result.ValidationReport.Diagnostics.ShouldContain(d => d.Code == QueryDiagnosticCodes.UnknownTable);
        result.ValidationReport.OutputColumns.ShouldBeEmpty();
    }

    [Fact]
    public async Task GrantedCaller_StillGetsColumnEvaluation()
    {
        var service = new QueryAnalysisService(
            Wp0.Tables(Orders(DatasetVisibility.Summary)),
            Wp0.Consents(Wp0.Allow(new TableIdentifier("sales", "dbo", "orders"), "S-OK", Wp0.TenantA)));

        var result = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromSql("SELECT * FROM sales.dbo.orders", name: "q")], Wp0.User("S-OK", "tenant-a"));

        result.ValidationReport.OutputColumns.Select(c => c.Column).ShouldBe(["id", "salary"], ignoreOrder: true);
    }

    // BundledConsentRequestService: a column-level item for a table without grant is not validated per column, so a ghost
    // column and a real one produce the same ticket (no existence oracle).
    [Fact]
    public async Task BundledRequest_ColumnItem_ForUnconsentedTable_DoesNotValidatePerColumn()
    {
        var service = new BundledConsentRequestService(Wp0.Tables(Orders(DatasetVisibility.Summary)), Wp0.Consents());
        var requester = Wp0.User("S-NOBODY", "tenant-a");

        var real = await service.SubmitBundledRequestAsync(requester,
            new BundledConsentRequest([new RequestableConsentItem("sales.dbo.orders", "salary", "READ_CLEARTEXT", "why")], "purpose"));
        var ghost = await service.SubmitBundledRequestAsync(requester,
            new BundledConsentRequest([new RequestableConsentItem("sales.dbo.orders", "ghost_column", "READ_CLEARTEXT", "why")], "purpose"));

        real.Tickets.Count.ShouldBe(1);
        ghost.Tickets.Count.ShouldBe(1);
        real.Tickets[0].Datasets.ShouldBe(ghost.Tickets[0].Datasets);
    }
}
