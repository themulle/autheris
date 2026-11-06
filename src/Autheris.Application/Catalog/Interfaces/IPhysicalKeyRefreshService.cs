namespace Autheris.Application.Catalog.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Service for refreshing and reconciling physical key directions and non-unique indexes for a datasource.
/// </summary>
public interface IPhysicalKeyRefreshService
{
    Task<PhysicalKeyRefreshResult> RefreshDatasourceKeysAsync(
        string datasourceId,
        PhysicalKeyRefreshRequest request,
        Sid actor,
        TenantId? restrictToTenant,
        CancellationToken ct = default);

    Task<PhysicalKeyRefreshResult> RefreshDatasourceKeysAsync(
        string datasourceId,
        PhysicalKeyRefreshRequest request,
        Sid actor,
        CancellationToken ct = default);
}
