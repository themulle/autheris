namespace Autheris.Infrastructure.Persistence.Products;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Products;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

internal static class DataProductStoreOperations
{
    private static TenantId Tenant(TenantId tenant, GovernanceTable table) => TenantScope.RequireTenant(tenant, table.ToSqlName());

    public static async Task<DataProduct?> GetAsync(GovernanceRepository repo, TenantId tenant, string schema, string name, CancellationToken ct)
    {
        var scoped = Tenant(tenant, GovernanceTable.DataProducts);
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ReadOneAsync(repo, conn, null, scoped, DataProductStoreSql.Normalize(schema), DataProductStoreSql.Normalize(name), ct).ConfigureAwait(false);
    }

    public static async Task<DataProduct?> GetByIdAsync(GovernanceRepository repo, TenantId tenant, Guid productId, CancellationToken ct)
    {
        if (productId == Guid.Empty)
        {
            return null;
        }

        var scoped = Tenant(tenant, GovernanceTable.DataProducts);
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = repo.CreateProductCommand(conn, null, DataProductStoreSql.GetByIdSql);
        cmd.AddBound("@tenant_id", scoped.Value);
        cmd.AddBound("@id", productId.ToString("D"));
        await using var r = await repo.ExecuteReaderAsync(cmd, "Governance.Products.GetById", ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? DataProductStoreSql.ReadProduct(r, scoped) : null;
    }

    private static async Task<DataProduct?> ReadOneAsync(GovernanceRepository repo, DbConnection conn, DbTransaction? tx, TenantId tenant, string schema, string name, CancellationToken ct)
    {
        await using var cmd = repo.CreateProductCommand(conn, tx, DataProductStoreSql.GetSql);
        DataProductStoreSql.BindKey(cmd, tenant, schema, name);
        await using var r = await repo.ExecuteReaderAsync(cmd, "Governance.Products.Get", ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? DataProductStoreSql.ReadProduct(r, tenant) : null;
    }

    public static async Task<IReadOnlyList<DataProduct>> ListAsync(GovernanceRepository repo, TenantId tenant, string? afterKey, int limit, CancellationToken ct)
    {
        var scoped = Tenant(tenant, GovernanceTable.DataProducts);
        var (afterSchema, afterName) = SplitKey(afterKey);
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = repo.CreateProductCommand(conn, null, repo.Dialect is PostgreSqlGovernanceDialect ? DataProductStoreSql.ListPgSql : DataProductStoreSql.ListSsSql);
        cmd.AddBound("@tenant_id", scoped.Value);
        cmd.AddBound("@after_schema", afterSchema);
        cmd.AddBound("@after_name", afterName);
        cmd.AddBound("@limit", Math.Clamp(limit, 1, 200), DbType.Int32);
        await using var r = await repo.ExecuteReaderAsync(cmd, "Governance.Products.List", ct).ConfigureAwait(false);
        var items = new List<DataProduct>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(DataProductStoreSql.ReadProduct(r, scoped));
        }

        return items;
    }

    public static async Task<IReadOnlyList<DataProduct>> ListScheduledAsync(GovernanceRepository repo, DataProduct? after, int limit, CancellationToken ct)
    {
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = repo.CreateProductCommand(conn, null, repo.Dialect is PostgreSqlGovernanceDialect ? DataProductStoreSql.ListScheduledPgSql : DataProductStoreSql.ListScheduledSsSql);
        cmd.AddBound("@after_tenant", after?.Tenant.Value ?? string.Empty);
        cmd.AddBound("@after_schema", after?.Schema ?? string.Empty);
        cmd.AddBound("@after_name", after?.Name ?? string.Empty);
        cmd.AddBound("@limit", Math.Clamp(limit, 1, 200), DbType.Int32);
        await using var r = await repo.ExecuteReaderAsync(cmd, "Governance.Products.ListScheduled", ct).ConfigureAwait(false);
        var items = new List<DataProduct>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(DataProductStoreSql.ReadProduct(r, new TenantId(r.GetString(0)), 1));
        }

        return items;
    }

    private static (string Schema, string Name) SplitKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return (string.Empty, string.Empty);
        }

        var at = key.IndexOf('/', StringComparison.Ordinal);
        return at < 0 ? (DataProductStoreSql.Normalize(key), string.Empty) : (DataProductStoreSql.Normalize(key[..at]), DataProductStoreSql.Normalize(key[(at + 1)..]));
    }

    public static Task<ProductWriteOutcome> CreateAsync(GovernanceRepository repo, DataProduct product, DataProductVersion first, CancellationToken ct)
    {
        Tenant(product.Tenant, GovernanceTable.DataProducts);
        return RunInTxAsync(repo, async (conn, tx) =>
        {
            try
            {
                await using (var cmd = repo.CreateProductCommand(conn, tx, DataProductStoreSql.InsertSql))
                {
                    DataProductStoreSql.BindProduct(cmd, product);
                    await repo.ExecuteNonQueryAsync(cmd, "Governance.Products.Create", ct).ConfigureAwait(false);
                }

                await InsertVersionAsync(repo, conn, tx, product.Tenant, first, ct).ConfigureAwait(false);
            }
            catch (DbException ex) when (repo.Dialect.IsUniqueViolation(ex))
            {
                return (false, new ProductWriteOutcome(ProductWriteStatus.AlreadyExists));
            }

            return (true, new ProductWriteOutcome(ProductWriteStatus.Created, product));
        }, ct);
    }

    public static Task<ProductWriteOutcome> ReplaceAsync(GovernanceRepository repo, DataProduct product, int expectedVersion, DataProductVersion? revision, CancellationToken ct)
    {
        Tenant(product.Tenant, GovernanceTable.DataProducts);
        return RunInTxAsync(repo, async (conn, tx) =>
        {
            int affected;
            await using (var cmd = repo.CreateProductCommand(conn, tx, DataProductStoreSql.UpdateSql))
            {
                DataProductStoreSql.BindProduct(cmd, product with { Version = expectedVersion + 1 });
                cmd.AddBound("@expected_version", expectedVersion, DbType.Int32);
                affected = await repo.ExecuteNonQueryAsync(cmd, "Governance.Products.Replace", ct).ConfigureAwait(false);
            }

            if (affected == 0)
            {
                await using var probe = repo.CreateProductCommand(conn, tx, DataProductStoreSql.ExistsSql);
                DataProductStoreSql.BindKey(probe, product.Tenant, product.Schema, product.Name);
                var exists = await repo.ExecuteScalarAsync(probe, "Governance.Products.Exists", ct).ConfigureAwait(false) is not null;
                return (false, new ProductWriteOutcome(exists ? ProductWriteStatus.VersionMismatch : ProductWriteStatus.NotFound));
            }

            if (revision is not null)
            {
                await InsertVersionAsync(repo, conn, tx, product.Tenant, revision with { Version = expectedVersion + 1 }, ct).ConfigureAwait(false);
            }

            return (true, new ProductWriteOutcome(ProductWriteStatus.Replaced, product with { Version = expectedVersion + 1 }));
        }, ct);
    }

    private static async Task InsertVersionAsync(GovernanceRepository repo, DbConnection conn, DbTransaction tx, TenantId tenant, DataProductVersion version, CancellationToken ct)
    {
        await using var cmd = repo.CreateProductCommand(conn, tx, DataProductStoreSql.InsertVersionSql);
        DataProductStoreSql.BindVersion(cmd, tenant, version);
        await repo.ExecuteNonQueryAsync(cmd, "Governance.Products.InsertVersion", ct).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<DataProductVersion>> GetVersionsAsync(GovernanceRepository repo, TenantId tenant, Guid productId, CancellationToken ct)
    {
        var scoped = Tenant(tenant, GovernanceTable.DataProductVersions);
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = repo.CreateProductCommand(conn, null, DataProductStoreSql.SelectVersionsSql);
        cmd.AddBound("@tenant_id", scoped.Value);
        cmd.AddBound("@product_id", productId.ToString("D"));
        await using var r = await repo.ExecuteReaderAsync(cmd, "Governance.Products.GetVersions", ct).ConfigureAwait(false);
        var items = new List<DataProductVersion>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(DataProductStoreSql.ReadVersion(r));
        }

        return items;
    }

    private static async Task<T> RunInTxAsync<T>(GovernanceRepository repo, Func<DbConnection, DbTransaction, Task<(bool Commit, T Result)>> work, CancellationToken ct)
    {
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var (commit, result) = await work(conn, tx).ConfigureAwait(false);
            if (commit) await repo.CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            else await repo.RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await repo.RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }
}
