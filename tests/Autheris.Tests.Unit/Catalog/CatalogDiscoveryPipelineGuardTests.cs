namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Catalog.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #111 (F-C11, early review of DWP-2): the engine stamps every row, so a non-admin row without a recognised view is fed to the real REST
/// list, detail and search paths through the engine-output seam. Each test fails when the <c>ApplyShape</c> call in <c>VisibleAsync</c> is removed;
/// #138 INC-1 re-homes that call and keeps these tests as the check.
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogDiscoveryPipelineGuardTests
{
    private static TableMetadata Row(string name, CatalogView view) => Wp0.Table("sales", name, "Short " + name, ["id"]) with { CatalogView = view };

    private static CatalogDiscoveryService Service(params TableMetadata[] engineOutput)
    {
        var service = Wp0.Service(Wp0.Tables(engineOutput), Wp0.Consents(), Wp0.Options(TenancyMode.Multi, []));
        service.EngineOutputForTesting = (_, _, _) => Task.FromResult<IReadOnlyList<TableMetadata>>(engineOutput);
        return service;
    }

    private static RequestContext Subject() => Wp0.Context(Wp0.User("S-USER", "tenant-a"));
    private static RequestContext ClusterAdmin() => Wp0.Context(Wp0.User("S-ADMIN", null, ["ClusterAdmin"]));

    [Fact]
    public async Task List_Detail_Search_NonAdminRowWithoutRecognisedView_DoesNotExist()
    {
        var service = Service(Row("unmarked_t", CatalogView.Unmarked), Row("unknown_t", (CatalogView)42), Row("granted_t", CatalogView.Granted));

        (await service.ListDatasetsAsync(Subject())).Select(d => d.Table).ShouldBe(["granted_t"]);
        (await service.SearchCatalogAsync("sales", null, Subject())).Select(d => d.Table).ShouldBe(["granted_t"]);
        (await service.SearchCatalogAsync("", null, Subject())).Select(d => d.Table).ShouldBe(["granted_t"]);
        (await service.GetDatasetDetailAsync(new TableIdentifier("sales", "dbo", "unmarked_t"), Subject())).ShouldBeNull();
        (await service.GetDatasetDetailAsync(new TableIdentifier("sales", "dbo", "unknown_t"), Subject())).ShouldBeNull();
        (await service.GetDatasetDetailAsync(new TableIdentifier("sales", "dbo", "granted_t"), Subject())).ShouldNotBeNull();
    }

    [Fact]
    public async Task List_AdminKeepsUnmarkedRows()
    {
        var service = Service(Row("unmarked_t", CatalogView.Unmarked));

        (await service.ListDatasetsAsync(ClusterAdmin())).Select(d => d.Table).ShouldBe(["unmarked_t"]);
    }
}
