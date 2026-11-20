using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Tests.Unit.TestDoubles;

internal sealed class StubCatalogDiscoveryService : ICatalogDiscoveryService
{
    public Func<DatasourceRegistrationRequest, RequestContext, Task<DatasourceRegistrationResult>>? OnRegisterDatasource { get; init; }
    public List<DatasourceRegistrationRequest> RegisterCalls { get; } = [];

    public Task<IReadOnlyList<CatalogDatasetSummary>> ListDatasetsAsync(RequestContext context, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<DiscoveryFacets> ListFacetsAsync(RequestContext context, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<CatalogDatasetDetail?> GetDatasetDetailAsync(TableIdentifier table, RequestContext context, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<CatalogDatasetSummary>> SearchCatalogAsync(string query, string? domain, RequestContext context, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<CatalogSearchHit>> SearchCatalogDetailedAsync(CatalogSearchQuery query, RequestContext context, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<CatalogDatasourceSummary>> ListDatasourcesAsync(RequestContext context, CancellationToken ct = default)
        => throw new NotSupportedException();

    public Task<DatasourceRegistrationResult> RegisterDatasourceAsync(DatasourceRegistrationRequest request, RequestContext context, CancellationToken ct = default)
    {
        RegisterCalls.Add(request);
        return OnRegisterDatasource is not null
            ? OnRegisterDatasource(request, context)
            : Task.FromResult(new DatasourceRegistrationResult("ds-1", request.Name, request.Domain, true, false, 0, [], [], [], true));
    }
}
