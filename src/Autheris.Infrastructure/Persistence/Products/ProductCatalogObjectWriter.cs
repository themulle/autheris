namespace Autheris.Infrastructure.Persistence.Products;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Products;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

/// <summary>
/// Product-only catalog write. The regular catalog write path rejects the reserved product source; this path accepts exactly the product
/// identity of the given tenant and then writes through the same chunked, transactional upsert (version bump included).
/// </summary>
public sealed class ProductCatalogObjectWriter(GovernanceRepository repository) : IProductCatalogObjectWriter
{
    private readonly GovernanceRepository _repo = repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task<TableMetadata> UpsertAsync(TenantId tenant, TableMetadata metadata, CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenant, GovernanceTable.Tables.ToSqlName());
        ProductSource.EnsureProductObject(tenant, metadata);
        metadata.ValidateRequiredFilterColumns();
        var results = await _repo.UpsertValidatedBatchAsync([metadata], ct).ConfigureAwait(false);
        return results[0].Metadata;
    }
}
