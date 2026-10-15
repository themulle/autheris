namespace Autheris.Application.Catalog;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Tenant-scoped catalog lookup port that avoids full catalog scans on per-request hot paths while enforcing strict tenant isolation.
/// </summary>
public interface ICatalogLookup
{
    Task<TableMetadata?> FindByIdentifierAsync(TenantId tenantId, TableIdentifier identifier, CancellationToken ct = default);

    Task<TableMetadata?> FindByEntityNameAsync(TenantId tenantId, string entityName, CancellationToken ct = default);

    Task<IReadOnlyList<TableMetadata>> GetTenantTablesAsync(TenantId tenantId, CancellationToken ct = default);

    void Invalidate(TenantId? tenantId = null);
}
