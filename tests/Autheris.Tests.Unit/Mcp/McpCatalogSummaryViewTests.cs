namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Catalog;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #111 WP-3 (A-4, A-1 G1, F-C9, F-C11, F-C12, BC-14): MCP output shaping. The allow-listed <c>Summary</c> description, admin rows that
/// keep <see cref="CatalogView.Unmarked"/>, non-admin rows without a recognised view, the tier-Full <c>sample_rows</c> term and tag reads.
/// </summary>
[Trait("Category", "Security")]
public sealed class McpCatalogSummaryViewTests
{
    private const string SharedId = "sales.dbo.shared_t";
    private static readonly string[] SharedList = [SharedId];

    private static TableMetadata Tier(string name, DatasetVisibility visibility, bool release = false, params string[] tags)
    {
        var t = Wp0.Table("sales", name, "Short text", ["id", "secret_col", "pii_col"]);
        return t with
        {
            Table = t.Table with { Visibility = visibility, LongDescription = "mentions secret_col and pii_col", Sensitivity = "CONFIDENTIAL", RequiresFourEyes = true },
            ReleaseProfileToCatalogViewers = release,
            CatalogTags = tags
        };
    }

    private static GatewayOptions Opts(TenancyMode mode, params string[] shared) => new()
    {
        Tenancy = new TenancyOptions { Mode = mode.ToString() },
        Catalog = new DataCatalogOptions { SharedTables = shared.Length == 0 ? SharedList : shared }
    };

    private static McpDatasetCatalog Catalog(GatewayOptions options, IEnumerable<TableMetadata> tables, IDataOwnershipRepository? owners = null, params Consent[] consents)
    {
        var graphQl = Substitute.For<IGraphQlCatalogMap>();
        graphQl.GetTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(ci =>
            Task.FromResult<GraphQlTableMapping?>(new GraphQlTableMapping(ci.Arg<TableIdentifier>(), "salesQuery", "T", "TFilter", "TOrder", [], [])));
        return new McpDatasetCatalog(
            Wp0.Tables(tables.ToArray()), Wp0.Consents(consents), Substitute.For<IGatewayExecutionService>(), Substitute.For<IAuditLogRepository>(),
            options: Options.Create(options), graphQlMap: graphQl, dataOwnershipRepository: owners);
    }

    private static ClaimsPrincipal Subject(string tenant = "tenant-a") => Wp0.User("S-USER", tenant);

    // --- Summary allow-list ---

    [Fact]
    public async Task Summary_Describe_IsBuiltFromTheAllowList()
    {
        var owners = Substitute.For<IDataOwnershipRepository>();
        owners.GetDataOwnersForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<DataOwner>>(
        [
            new DataOwner { DisplayName = "Olga Owner", Email = "olga@corp.example", AdAccount = "CORP\\olga" },
            new DataOwner { DisplayName = "Dan Delegate", Email = "dan@corp.example", AdAccount = "CORP\\dan" },
            new DataOwner { DisplayName = "", Email = "nameless@corp.example", AdAccount = "CORP\\nameless" }
        ]));
        var catalog = Catalog(Opts(TenancyMode.Multi), [Tier("shared_t", DatasetVisibility.Summary, release: true, "finance", "pii")], owners);

        var d = await catalog.DescribeDatasetAsync(Subject(), SharedId);

        d.View.ShouldBe("summary");
        d.Id.ShouldBe(SharedId);
        d.Description.ShouldBe("Short text");
        d.Sensitivity.ShouldBe("CONFIDENTIAL");
        d.RequiresApproval.ShouldBeTrue();
        d.Tags.ShouldBe(["finance", "pii"]);
        d.AccessStatus.ShouldBe("NONE");
        d.DataOwner.ShouldBe("Olga Owner");
        d.DataOwnerDelegates.ShouldBe(["Dan Delegate"]);
        d.LongDescription.ShouldBeNull();
        d.Columns.ShouldBeEmpty();
        d.Examples.ShouldBeEmpty();
        d.GraphQl.ShouldBeNull();
        d.OtherAccess.ShouldBeNull();
        d.Indexes.ShouldBeNull();
        System.Text.Json.JsonSerializer.Serialize(d).ShouldNotContain("secret_col");
        System.Text.Json.JsonSerializer.Serialize(d).ShouldNotContain("@");
    }

    [Fact]
    public void Summary_AllowList_PinsTheExactPropertySet()
    {
        // A property added to the description is excluded from the Summary until it is classified here (BC-14).
        var nonAllowListed = new[] { "LongDescription", "Columns", "Examples", "GraphQl", "OtherAccess", "Indexes" };
        var allowListed = new[]
        {
            "Id", "Domain", "Schema", "Table", "Description", "Sensitivity", "RequiresApproval", "Tags", "DataOwner", "DataOwnerDelegates", "AccessStatus", "View"
        };

        var all = typeof(McpDatasetDescription).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).Where(n => n != "EqualityContract").OrderBy(n => n).ToList();

        all.ShouldBe(nonAllowListed.Concat(allowListed).OrderBy(n => n).ToList());
    }

    [Fact]
    public async Task Summary_List_ReportsNoColumnsAndNoGraphQlField_AndTagsComeFromCatalogTags()
    {
        var catalog = Catalog(Opts(TenancyMode.Multi), [Tier("shared_t", DatasetVisibility.Summary, false, "finance")]);

        var list = await catalog.ListDatasetsAsync(Subject());

        var row = list.Datasets.ShouldHaveSingleItem();
        row.ColumnCount.ShouldBe(0);
        row.GraphQlField.ShouldBeNull();
        row.Tags.ShouldBe(["finance"]);
        (await catalog.ListDatasetsAsync(Subject(), tag: "FINANCE")).Datasets.Count.ShouldBe(1);
        (await catalog.ListDatasetsAsync(Subject(), tag: "other")).Datasets.ShouldBeEmpty();
    }

    [Fact]
    public async Task Summary_Search_NeverMatchesAColumnName_FullDoes()
    {
        var catalog = Catalog(Opts(TenancyMode.Multi, SharedId, "sales.dbo.full_t"),
            [Tier("shared_t", DatasetVisibility.Summary), Tier("full_t", DatasetVisibility.Full)]);

        var hits = await catalog.ListDatasetsAsync(Subject(), search: "secret_col");

        hits.Datasets.Select(d => d.Id).ShouldBe(["sales.dbo.full_t"]);
    }

    [Fact]
    public async Task Full_DescribeHasColumns_AndNoSummaryMarker()
    {
        var catalog = Catalog(Opts(TenancyMode.Multi), [Tier("shared_t", DatasetVisibility.Full)]);

        var d = await catalog.DescribeDatasetAsync(Subject(), SharedId);

        d.View.ShouldBeNull();
        d.Columns.Count.ShouldBe(3);
        d.LongDescription.ShouldNotBeNull();
    }

    // --- G1 / F-C11 / F-C12 ---

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
        // The persisted tier is Summary, but an administrator view is never narrowed (accepted behaviour, pinned).
        var catalog = Catalog(Opts(mode), [Tier("shared_t", DatasetVisibility.Summary)], consents: Wp0.Allow(new TableIdentifier("sales", "dbo", "shared_t"), "x", Wp0.TenantA));

        var d = await catalog.DescribeDatasetAsync(principal, SharedId);

        d.View.ShouldBeNull();
        d.Columns.Count.ShouldBe(3);
        d.LongDescription.ShouldBe("mentions secret_col and pii_col");
        (await catalog.ListDatasetsAsync(principal)).Datasets.Single().ColumnCount.ShouldBe(3);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("claimless")]
    [InlineData("none")]
    public async Task NonAdminRow_Unmarked_TreatedAsNotFound(string caller)
    {
        // An untiered table (Hidden, row view Unmarked at rest) is invisible to every non-admin class on MCP.
        var principal = caller switch
        {
            "subject" => Subject(),
            "claimless" => Wp0.User("S-USER", null),
            _ => new ClaimsPrincipal(new ClaimsIdentity())
        };
        var catalog = Catalog(Opts(TenancyMode.Multi), [Tier("shared_t", DatasetVisibility.Hidden)]);

        (await catalog.ListDatasetsAsync(principal)).Datasets.ShouldBeEmpty();
        await Should.ThrowAsync<TableNotFoundException>(() => catalog.DescribeDatasetAsync(principal, SharedId));
    }

    [Fact]
    public void ShapeStage_NonAdminUnmarkedOrUnknownView_IsNotFound_AdminIsFull()
    {
        var row = Tier("shared_t", DatasetVisibility.Full);

        McpDatasetCatalog.Shape(false, row with { CatalogView = CatalogView.Unmarked }).ShouldBe(CatalogOutputShape.NotFound);
        McpDatasetCatalog.Shape(false, row with { CatalogView = (CatalogView)42 }).ShouldBe(CatalogOutputShape.NotFound);
        McpDatasetCatalog.Shape(true, row with { CatalogView = CatalogView.Unmarked }).ShouldBe(CatalogOutputShape.Full);
    }

    [Fact]
    public async Task Claimless_GovernanceAdmin_Multi_NeverFullOutput()
    {
        var claimless = Wp0.User("gov", null, ["GovernanceAdmin"]);
        var catalog = Catalog(Opts(TenancyMode.Multi), [Tier("shared_t", DatasetVisibility.Summary), Tier("other_t", DatasetVisibility.Full)]);

        // No tier for a claim-less caller in Multi, no administrator view: nothing at all.
        (await catalog.ListDatasetsAsync(claimless)).Datasets.ShouldBeEmpty();
        await Should.ThrowAsync<TableNotFoundException>(() => catalog.DescribeDatasetAsync(claimless, SharedId));
    }

    [Theory]
    [InlineData(TenancyMode.Multi, false)]
    [InlineData(TenancyMode.Single, true)]
    public async Task Shaping_IsAdmin_ComesFromSameResolverAsListFilter(TenancyMode mode, bool expectFull)
    {
        // The same principal (claim-less GovernanceAdmin) is Claimless in Multi and Unrestricted in Single; list and shaping agree.
        var principal = Wp0.User("gov", null, ["GovernanceAdmin"]);
        var catalog = Catalog(Opts(mode), [Tier("shared_t", DatasetVisibility.Summary)]);

        var list = await catalog.ListDatasetsAsync(principal);

        if (expectFull)
        {
            list.Datasets.Single().ColumnCount.ShouldBe(3);
            (await catalog.DescribeDatasetAsync(principal, SharedId)).View.ShouldBeNull();
        }
        else
        {
            list.Datasets.ShouldBeEmpty();
        }
    }

    // --- sample_rows third term ---

    [Theory]
    [InlineData(DatasetVisibility.Full, true, true)]
    [InlineData(DatasetVisibility.Full, false, false)]
    [InlineData(DatasetVisibility.Summary, true, false)]
    public async Task SampleRows_Synthetic_NoConsent_AllowedOnlyForFullWithRelease(DatasetVisibility tier, bool release, bool allowed)
    {
        var catalog = Catalog(Opts(TenancyMode.Multi), [Tier("shared_t", tier, release)]);

        if (allowed)
        {
            var sample = await catalog.SampleRowsAsync(Subject(), SharedId, 2, synthetic: true);
            sample.IsSynthetic.ShouldBeTrue();
        }
        else
        {
            await Should.ThrowAsync<GatewayForbiddenException>(() => catalog.SampleRowsAsync(Subject(), SharedId, 2, synthetic: true));
        }
    }
}
