namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Products;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Products;

public partial class GovernanceRepository : IDataProductRepository
{
    internal System.Data.Common.DbCommand CreateProductCommand(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }
    public Task<DataProduct?> GetProductAsync(TenantId tenant, string schema, string name, CancellationToken ct = default)
        => DataProductStoreOperations.GetAsync(this, tenant, schema, name, ct);
    public Task<DataProduct?> GetProductByIdAsync(TenantId tenant, Guid productId, CancellationToken ct = default)
        => DataProductStoreOperations.GetByIdAsync(this, tenant, productId, ct);
    public Task<IReadOnlyList<DataProduct>> ListProductsAsync(TenantId tenant, string? afterKey, int limit, CancellationToken ct = default)
        => DataProductStoreOperations.ListAsync(this, tenant, afterKey, limit, ct);
    public Task<IReadOnlyList<DataProduct>> ListScheduledProductsAsync(DataProduct? after, int limit, CancellationToken ct = default)
        => DataProductStoreOperations.ListScheduledAsync(this, after, limit, ct);
    public Task<ProductWriteOutcome> CreateProductAsync(DataProduct product, DataProductVersion firstRevision, CancellationToken ct = default)
        => DataProductStoreOperations.CreateAsync(this, product ?? throw new ArgumentNullException(nameof(product)), firstRevision ?? throw new ArgumentNullException(nameof(firstRevision)), ct);
    public Task<ProductWriteOutcome> ReplaceProductAsync(DataProduct product, int expectedVersion, DataProductVersion? definitionRevision, CancellationToken ct = default)
        => DataProductStoreOperations.ReplaceAsync(this, product ?? throw new ArgumentNullException(nameof(product)), expectedVersion, definitionRevision, ct);
    public Task<IReadOnlyList<DataProductVersion>> GetProductVersionsAsync(TenantId tenant, Guid productId, CancellationToken ct = default)
        => DataProductStoreOperations.GetVersionsAsync(this, tenant, productId, ct);
}
