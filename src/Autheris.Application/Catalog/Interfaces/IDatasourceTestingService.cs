namespace Autheris.Application.Catalog.Interfaces;

using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Pre-flight connectivity, TLS, and authentication probing service for registered data sources.
/// </summary>
public interface IDatasourceTestingService
{
    Task<DatasourceTestResult> TestDatasourceAsync(
        string datasourceId,
        DatasourceTestRequest request,
        ClaimsPrincipal user,
        CancellationToken ct = default);
}
