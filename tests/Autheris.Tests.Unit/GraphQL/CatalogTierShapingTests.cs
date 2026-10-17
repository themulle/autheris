namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Types;
using Autheris.Tests.Unit.Catalog;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #111 WP-3 (A-4, A-1 G1, F-C9, F-C11, F-C12): GraphQL <c>catalog</c> shaping. Summary rows carry no columns, admin rows keep
/// <see cref="CatalogView.Unmarked"/> and full output, the OpenSchema branch is not shaped, tags come from <c>CatalogTags</c>.
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogTierShapingTests
{
    private static readonly string[] Shared = ["sales.dbo.sum_t", "sales.dbo.full_t", "sales.dbo.hid_t"];

    private static TableMetadata Tier(string name, DatasetVisibility visibility, params string[] tags)
    {
        var t = Wp0.Table("sales", name, "Short " + name, ["id", "secret_col"]);
        return t with { Table = t.Table with { Visibility = visibility, DisplayName = name }, CatalogTags = tags };
    }

    private static readonly TableMetadata[] Tables =
    [
        Tier("sum_t", DatasetVisibility.Summary, "finance"),
        Tier("full_t", DatasetVisibility.Full, "ops"),
        Tier("hid_t", DatasetVisibility.Hidden, "internal")
    ];

    private static GatewayOptions Opts(TenancyMode mode, bool openSchema = false) => new()
    {
        Tenancy = new TenancyOptions { Mode = mode.ToString() },
        Catalog = new DataCatalogOptions { SharedTables = Shared, OpenSchema = openSchema }
    };

    private static Task<IReadOnlyList<TableMetadataDto>> Catalog(ClaimsPrincipal user, GatewayOptions options, string? search = null, string? tag = null)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = user });
        return new Query().GetCatalogAsync(
            domain: null, first: null, after: null, search: search, tag: tag,
            Wp0.Tables(Tables), Wp0.Consents(), approvalRepository: null, dataOwnershipRepository: null,
            accessor, Options.Create(options));
    }

    private static ClaimsPrincipal Subject(string? tenant = "tenant-a") => Wp0.User("S-USER", tenant);

    [Fact]
    public async Task Summary_HasNoColumns_Full_HasColumns_Hidden_Absent_TagsFromCatalogTags()
    {
        var result = await Catalog(Subject(), Opts(TenancyMode.Multi));

        result.Select(r => r.TableName).OrderBy(n => n).ShouldBe(["full_t", "sum_t"]);
        var summary = result.Single(r => r.TableName == "sum_t");
        summary.Columns.ShouldBeEmpty();
        summary.Description.ShouldBe("Short sum_t");
        summary.Tags.ShouldBe(["finance"]);
        result.Single(r => r.TableName == "full_t").Columns.ShouldBe(["id", "secret_col"]);
        (await Catalog(Subject(), Opts(TenancyMode.Multi), tag: "FINANCE")).Select(r => r.TableName).ShouldBe(["sum_t"]);
    }

    [Fact]
    public async Task Summary_Search_NeverMatchesAColumn()
    {
        var result = await Catalog(Subject(), Opts(TenancyMode.Multi), search: "secret_col");

        result.Select(r => r.TableName).ShouldBe(["full_t"]);
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

        var result = await Catalog(principal, Opts(mode));

        result.ShouldNotBeEmpty();
        result.ShouldAllBe(r => r.Columns.Count == 2);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("claimless")]
    public async Task NonAdminRow_Unmarked_TreatedAsNotFound(string caller)
    {
        // Untiered tables: no tier row exists for the non-administrator, nothing leaks.
        var hiddenOnly = new[] { Tier("hid_t", DatasetVisibility.Hidden) };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = caller == "subject" ? Subject() : Subject(null) });

        var result = await new Query().GetCatalogAsync(null, null, null, null, null, Wp0.Tables(hiddenOnly), Wp0.Consents(), null, null, accessor, Options.Create(Opts(TenancyMode.Multi)));

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task NonAdminRow_Unmarked_NoneCaller_IsRefused()
    {
        // An invalid tenant claim resolves to the None class and is refused outright.
        var none = Wp0.User("S-USER", "not a valid tenant!");

        await Should.ThrowAsync<GraphQLException>(() => Catalog(none, Opts(TenancyMode.Multi)));
    }

    [Fact]
    public void ShapeStage_NonAdminUnmarkedOrUnknownView_IsNull_AdminIsFull_SummaryHasNoColumns()
    {
        var row = Tier("full_t", DatasetVisibility.Full);

        Query.ToCatalogDto(row with { CatalogView = CatalogView.Unmarked }, isAdminView: false, "NONE").ShouldBeNull();
        Query.ToCatalogDto(row with { CatalogView = (CatalogView)42 }, isAdminView: false, "NONE").ShouldBeNull();
        Query.ToCatalogDto(row with { CatalogView = CatalogView.Unmarked }, isAdminView: true, "GRANTED")!.Columns.Count.ShouldBe(2);
        Query.ToCatalogDto(row with { CatalogView = CatalogView.Summary }, isAdminView: false, "NONE")!.Columns.ShouldBeEmpty();
    }

    [Fact]
    public async Task Claimless_GovernanceAdmin_Multi_NeverFullOutput()
    {
        var result = await Catalog(Wp0.User("gov", null, ["GovernanceAdmin"]), Opts(TenancyMode.Multi));

        result.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(TenancyMode.Multi, 0)]
    [InlineData(TenancyMode.Single, 3)]
    public async Task Shaping_IsAdmin_ComesFromSameResolverAsListFilter(TenancyMode mode, int expected)
    {
        var result = await Catalog(Wp0.User("gov", null, ["GovernanceAdmin"]), Opts(mode));

        result.Count.ShouldBe(expected);
        result.ShouldAllBe(r => r.Columns.Count == 2);
    }

    [Fact]
    public async Task OpenSchema_Branch_NotAffectedByTierShaping()
    {
        // The operator opt-in lists every table, Summary and Hidden included, with columns (bypasses tiers and consents by design).
        var result = await Catalog(Subject(), Opts(TenancyMode.Multi, openSchema: true));

        result.Count.ShouldBe(3);
        result.ShouldAllBe(r => r.Columns.Count == 2);
        result.Single(r => r.TableName == "hid_t").Tags.ShouldBe(["internal"]);
    }
}
