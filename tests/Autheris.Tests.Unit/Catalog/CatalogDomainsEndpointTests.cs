namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Catalog.Discovery;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class CatalogDomainsEndpointTests
{
    [Fact]
    public async Task ListFacetsAsync_WithoutDiscoveryService_FallsBackToVisibleTables()
    {
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", "orders", schema: "dbo"));
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", "refunds", schema: "analytics"));
        var hiddenForeign = DiscoveryTestBuilder.MakeTable("foreign", "secret", ownerTenantId: "tenant-b");
        var options = Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() });
        var catalogService = new CatalogDiscoveryService(
            repo,
            new FakeAuditWriter(),
            options,
            visibilityService: new FakeCatalogVisibilityService([repo.Tables[0], repo.Tables[1]], [repo.Tables[0], repo.Tables[1], hiddenForeign]),
            discoveryService: null);

        var principal = DiscoveryTestBuilder.User("S-1", "tenant-a");
        var requestContext = new RequestContext(new TenantId("tenant-a"), new Sid("S-1"), User: principal);

        var facets = await catalogService.ListFacetsAsync(requestContext);

        facets.TotalCount.ShouldBe(2);
        facets.Domains.ShouldHaveSingleItem();
        facets.Domains[0].Domain.ShouldBe("sales");
        facets.Domains[0].Count.ShouldBe(2);
    }

    private sealed class FakeCatalogVisibilityService(
        System.Collections.Generic.IReadOnlyList<TableMetadata> permittedTables,
        System.Collections.Generic.IReadOnlyList<TableMetadata>? allTables = null)
        : ICatalogVisibilityService
    {
        public ValueTask<CatalogVisibleSet> GetVisibleAsync(
            CatalogCaller caller,
            Autheris.Application.Governance.Contracts.SchemaContractDefinition? contract = null,
            CancellationToken ct = default)
            => ValueTask.FromResult(new CatalogVisibleSet(permittedTables, CallerScope.Tenant, isAdminView: false, allTables));

        public ValueTask<CatalogVisibleRow?> FindVisibleAsync(CatalogCaller caller, TableIdentifier table, CancellationToken ct = default)
            => ValueTask.FromResult<CatalogVisibleRow?>(null);

        public ValueTask<System.Collections.Generic.IReadOnlySet<TableIdentifier>> FilterVisibleAsync(
            CatalogCaller caller,
            System.Collections.Generic.IReadOnlyCollection<TableIdentifier> candidates,
            CancellationToken ct = default)
            => ValueTask.FromResult<System.Collections.Generic.IReadOnlySet<TableIdentifier>>(new System.Collections.Generic.HashSet<TableIdentifier>());
    }
}
