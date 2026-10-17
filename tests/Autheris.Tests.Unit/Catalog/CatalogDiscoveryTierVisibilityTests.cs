namespace Autheris.Tests.Unit.Catalog;

using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #111 WP-5 (A-4, A-1 G1, F-C11): the REST discovery service applies the persisted tiers (Discovery surface), shapes Summary details
/// without columns, never lets a column name match a Summary row, and keeps the admin views unmarked and unnarrowed.
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogDiscoveryTierVisibilityTests
{
    private static readonly string[] SharedList = ["sales.dbo.sum_t", "sales.dbo.full_t", "sales.dbo.hid_t"];

    private static TableMetadata Tier(string name, DatasetVisibility visibility, params string[] deniedColumns)
    {
        var t = Wp0.Table("sales", name, "Short " + name, ["id", "secret_col"], masking: deniedColumns.ToDictionary(
            c => c, _ => new MaskingRule { RuleType = "Deny" }));
        return t with
        {
            Table = t.Table with { Visibility = visibility, Sensitivity = "CONFIDENTIAL" },
            PrimaryKeyColumns = ["id"],
            Indexes = [new TableIndexDefinition("ix_secret", ["secret_col"], false)]
        };
    }

    private static readonly TableMetadata[] Tables =
    [
        Tier("sum_t", DatasetVisibility.Summary),
        Tier("full_t", DatasetVisibility.Full),
        Tier("hid_t", DatasetVisibility.Hidden)
    ];

    private static GatewayOptions Opts(TenancyMode mode) => Wp0.Options(mode, SharedList);

    private static CatalogDiscoveryService Service(GatewayOptions options, TableMetadata[]? tables = null, Autheris.Domain.Interfaces.ICatalogSearchEngine? engine = null) =>
        Wp0.Service(Wp0.Tables(tables ?? Tables), Wp0.Consents(), options, engine);

    private static RequestContext Subject(string? tenant = "tenant-a") => Wp0.Context(Wp0.User("S-USER", tenant));

    [Fact]
    public async Task List_ShowsSummaryAndFull_NeverHidden()
    {
        var list = await Service(Opts(TenancyMode.Multi)).ListDatasetsAsync(Subject());

        list.Select(d => d.Table).OrderBy(n => n).ShouldBe(["full_t", "sum_t"]);
    }

    [Fact]
    public async Task Detail_Summary_HasNoColumnsKeysOrIndexes_Full_HasColumns_Hidden_IsNull()
    {
        var service = Service(Opts(TenancyMode.Multi));
        var id = (string n) => new TableIdentifier("sales", "dbo", n);

        var summary = await service.GetDatasetDetailAsync(id("sum_t"), Subject());
        summary.ShouldNotBeNull();
        summary.Columns.ShouldBeEmpty();
        summary.PrimaryKeys.ShouldBeEmpty();
        (summary.Indexes ?? []).ShouldBeEmpty();
        summary.Sensitivity.ShouldBe("CONFIDENTIAL");
        System.Text.Json.JsonSerializer.Serialize(summary).ShouldNotContain("secret_col");

        var full = await service.GetDatasetDetailAsync(id("full_t"), Subject());
        full!.Columns.Select(c => c.Name).ShouldBe(["id", "secret_col"]);

        (await service.GetDatasetDetailAsync(id("hid_t"), Subject())).ShouldBeNull();
    }

    [Fact]
    public async Task Detail_Full_WithATableLevelDenyColumn_NeverListsIt()
    {
        var tables = new[] { Tier("full_t", DatasetVisibility.Full, "secret_col") };

        var full = await Service(Opts(TenancyMode.Multi), tables).GetDatasetDetailAsync(new TableIdentifier("sales", "dbo", "full_t"), Subject());

        full!.Columns.Select(c => c.Name).ShouldBe(["id"]);
        (full.Indexes ?? []).ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_Summary_NeverMatchesAColumnName_AndHasNoGraphQlField()
    {
        var engine = new CatalogSearchEngine(new LocalDeterministicEmbeddingGenerator(), Microsoft.Extensions.Options.Options.Create(new CatalogSearchOptions()), NullLogger<CatalogSearchEngine>.Instance);
        await engine.RebuildIndexAsync(Tables);
        var service = Service(Opts(TenancyMode.Multi), engine: engine);

        var byColumn = await service.SearchCatalogDetailedAsync(new CatalogSearchQuery("secret_col", Limit: 10, Mode: CatalogSearchMode.KeywordBm25), Subject());
        var byName = await service.SearchCatalogDetailedAsync(new CatalogSearchQuery("Short sum_t", Limit: 10, Mode: CatalogSearchMode.KeywordBm25), Subject());

        byColumn.Select(h => h.TableIdentifier.TableName).ShouldNotContain("sum_t");
        var hit = byName.Single(h => h.TableIdentifier.TableName == "sum_t");
        hit.SuggestedGraphQlField.ShouldBeNull();
        hit.RelevantColumns.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("global")]
    [InlineData("tenant")]
    [InlineData("unrestricted")]
    public async Task AdminRow_UnmarkedView_GetsFullDescription(string admin)
    {
        var (mode, principal) = admin switch
        {
            "global" => (TenancyMode.Multi, Wp0.User("root", "tenant-a", ["ClusterAdmin"])),
            "tenant" => (TenancyMode.Multi, Wp0.User("gov", "tenant-a", ["GovernanceAdmin"])),
            _ => (TenancyMode.Single, Wp0.User("gov", null, ["GovernanceAdmin"]))
        };

        var detail = await Service(Opts(mode)).GetDatasetDetailAsync(new TableIdentifier("sales", "dbo", "sum_t"), Wp0.Context(principal));

        detail!.Columns.Select(c => c.Name).ShouldBe(["id", "secret_col"]);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("claimless")]
    [InlineData("none")]
    public async Task NonAdminRow_Unmarked_TreatedAsNotFound(string caller)
    {
        // Untiered table: invisible to every non-administrator class.
        var hiddenOnly = new[] { Tier("hid_t", DatasetVisibility.Hidden) };
        var context = caller switch
        {
            "subject" => Subject(),
            "claimless" => Subject(null),
            _ => new RequestContext(TenantId.LegacySingleTenant, new Sid("S-USER"), User: Wp0.User("S-USER", "not a valid tenant!"))
        };
        var service = Service(Opts(TenancyMode.Multi), hiddenOnly);

        (await service.ListDatasetsAsync(context)).ShouldBeEmpty();
        (await service.GetDatasetDetailAsync(new TableIdentifier("sales", "dbo", "hid_t"), context)).ShouldBeNull();
    }

    [Fact]
    public void ShapeStage_NonAdminUnmarkedOrUnknownView_IsDropped_AdminKept()
    {
        var unmarked = Tables[1] with { CatalogView = CatalogView.Unmarked };
        var unknown = Tables[1] with { CatalogView = (CatalogView)42 };

        CatalogDiscoveryService.ApplyShape([unmarked, unknown], isAdminView: false).ShouldBeEmpty();
        CatalogDiscoveryService.ApplyShape([unmarked, unknown], isAdminView: true).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Claimless_GovernanceAdmin_Multi_NeverFullOutput()
    {
        var context = Wp0.Context(Wp0.User("gov", null, ["GovernanceAdmin"]));
        var service = Service(Opts(TenancyMode.Multi));

        (await service.ListDatasetsAsync(context)).ShouldBeEmpty();
        (await service.GetDatasetDetailAsync(new TableIdentifier("sales", "dbo", "full_t"), context)).ShouldBeNull();
    }

    [Theory]
    [InlineData(TenancyMode.Multi, 0)]
    [InlineData(TenancyMode.Single, 3)]
    public async Task Shaping_IsAdmin_ComesFromSameResolverAsListFilter(TenancyMode mode, int expected)
    {
        var context = Wp0.Context(Wp0.User("gov", null, ["GovernanceAdmin"]));
        var service = Service(Opts(mode));

        var list = await service.ListDatasetsAsync(context);
        list.Count.ShouldBe(expected);
        if (expected > 0)
        {
            (await service.GetDatasetDetailAsync(new TableIdentifier("sales", "dbo", "sum_t"), context))!.Columns.Count.ShouldBe(2);
        }
    }
}
