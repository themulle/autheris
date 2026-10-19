namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Catalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Tests.Unit.TestSupport;
using NSubstitute;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #111 (F-C11, early review of DWP-2): the engine stamps every row, so a non-admin row without a recognised view is fed to the real REST
/// list, detail and search paths through the engine-output seam. Each test fails when the shape step of <c>CatalogVisibilityService</c> is removed
/// (#138 INC-1 re-homed it there; mutation-checked).
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogDiscoveryPipelineGuardTests
{
    private static TableMetadata Row(string name, CatalogView view) => Wp0.Table("sales", name, "Short " + name, ["id"]) with { CatalogView = view };

    private static CatalogDiscoveryService Service(params TableMetadata[] engineOutput)
    {
        var tables = Wp0.Tables(engineOutput);
        var consents = Wp0.Consents();
        var options = Wp0.Options(TenancyMode.Multi, []);
        // The real service with the engine-output seam: the shape step inside the service is what these tests guard.
        var visibility = CatalogVisibilityTestFactory.Create(
            tables, consents, options, engineOutput: (_, _, _) => Task.FromResult<IReadOnlyList<TableMetadata>>(engineOutput));
        return new CatalogDiscoveryService(
            tables, Substitute.For<IAuditLogRepository>(), Microsoft.Extensions.Options.Options.Create(options), visibility);
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
