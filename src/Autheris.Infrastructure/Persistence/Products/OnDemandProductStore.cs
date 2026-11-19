namespace Autheris.Infrastructure.Persistence.Products;

using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Products;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

/// <summary>Cross-tenant listing of the <c>onDemand</c> data product views for the cluster-wide idle drop (read only, keyset paged).</summary>
public sealed class OnDemandProductStore(GovernanceRepository repository) : IOnDemandProductStore
{
    private const string Columns = "id, product_schema, product_name, kind, definition, definition_hash, base_objects_json, columns_json, created_by, authority, status, managed_by, load_json, limits_json, description, product_owner, version, created_at, updated_at";
    private const string Where = " FROM DATA_PRODUCTS WHERE kind = 'View' AND status <> 'Retired' AND load_json LIKE '%\"Mode\":\"OnDemand\"%' AND (tenant_id > @after_tenant OR (tenant_id = @after_tenant AND (product_schema > @after_schema OR (product_schema = @after_schema AND product_name > @after_name)))) ORDER BY tenant_id, product_schema, product_name";
    private const string PgSql = "SELECT tenant_id, " + Columns + Where + " LIMIT @limit";
    private const string SsSql = "SELECT TOP (@limit) tenant_id, " + Columns + Where;

    private readonly GovernanceRepository _repo = repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task<IReadOnlyList<DataProduct>> ListOnDemandProductsAsync(DataProduct? after, int limit, CancellationToken ct = default)
    {
        await using var conn = await _repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = _repo.CreateProductCommand(conn, null, _repo.Dialect is PostgreSqlGovernanceDialect ? PgSql : SsSql);
        cmd.AddBound("@after_tenant", after?.Tenant.Value ?? string.Empty);
        cmd.AddBound("@after_schema", after?.Schema ?? string.Empty);
        cmd.AddBound("@after_name", after?.Name ?? string.Empty);
        cmd.AddBound("@limit", Math.Clamp(limit, 1, 200), DbType.Int32);
        await using var r = await _repo.ExecuteReaderAsync(cmd, "Governance.Products.ListOnDemand", ct).ConfigureAwait(false);
        var items = new List<DataProduct>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(DataProductStoreSql.ReadProduct(r, new TenantId(r.GetString(0)), 1));
        }

        return items;
    }
}
