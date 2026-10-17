namespace Autheris.Tests.Unit.Mcp;

using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Services;
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
/// Issue #111 (F-C11, early review of DWP-2): pipeline-level guards. The engine stamps every row it returns, so a non-admin row without a
/// recognised view is fed to the real list and describe paths through the engine-output seam. Each test fails when its guard is removed
/// (list filter, <c>FindVisibleAsync</c> shape check); #138 INC-1 must re-home these guards and keeps these tests as the check.
/// </summary>
[Trait("Category", "Security")]
public sealed class McpCatalogPipelineGuardTests
{
    private static TableMetadata Row(string name, CatalogView view)
    {
        var t = Wp0.Table("sales", name, "Short text", ["id"]);
        return t with { CatalogView = view };
    }

    private static McpDatasetCatalog Catalog(params TableMetadata[] engineOutput)
    {
        var catalog = new McpDatasetCatalog(
            Wp0.Tables(engineOutput), Wp0.Consents(), Substitute.For<IGatewayExecutionService>(), Substitute.For<IAuditLogRepository>(),
            options: Options.Create(new GatewayOptions { Tenancy = new TenancyOptions { Mode = "Multi" } }));
        catalog.VisibleTablesForTesting = (_, _) => Task.FromResult<IReadOnlyList<TableMetadata>>(engineOutput);
        return catalog;
    }

    private static ClaimsPrincipal Subject() => Wp0.User("S-USER", "tenant-a");
    private static ClaimsPrincipal ClusterAdmin() => Wp0.User("S-ADMIN", null, ["ClusterAdmin"]);

    [Fact]
    public async Task List_NonAdminRowWithoutRecognisedView_IsDropped_BeforeTotalAndPaging()
    {
        var catalog = Catalog(Row("a_unmarked", CatalogView.Unmarked), Row("b_granted", CatalogView.Granted), Row("c_unknown", (CatalogView)42));

        var list = await catalog.ListDatasetsAsync(Subject(), limit: 1);

        list.Total.ShouldBe(1);
        list.Truncated.ShouldBeFalse();
        list.NextOffset.ShouldBeNull();
        list.Datasets.Select(d => d.Id).ShouldBe(["sales.dbo.b_granted"]);
    }

    [Fact]
    public async Task List_AdminKeepsUnmarkedRows()
    {
        var catalog = Catalog(Row("a_unmarked", CatalogView.Unmarked));

        (await catalog.ListDatasetsAsync(ClusterAdmin())).Total.ShouldBe(1);
    }

    [Fact]
    public async Task Describe_NonAdminRowWithoutRecognisedView_IsTheSameNotFoundAsAMissingTable()
    {
        var catalog = Catalog(Row("a_unmarked", CatalogView.Unmarked));

        var seen = await Should.ThrowAsync<TableNotFoundException>(() => catalog.DescribeDatasetAsync(Subject(), "sales.dbo.a_unmarked"));
        var missing = await Should.ThrowAsync<TableNotFoundException>(() => catalog.DescribeDatasetAsync(Subject(), "sales.dbo.nothing"));

        seen.Message.Replace("a_unmarked", "x").ShouldBe(missing.Message.Replace("nothing", "x"));
    }
}
