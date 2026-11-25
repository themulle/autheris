namespace Autheris.Infrastructure.Persistence.Products;

using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Products;
using Autheris.Domain.Common;
using Autheris.Infrastructure.Persistence.Dialects;

/// <summary>
/// Owner link of product catalog objects (<c>TABLE_OWNERS</c>): the product owner is the one PRIMARY data owner of the object, none once
/// the product is retired. It writes only the object a tenant owns (the tenant schema of the identity must match the tenant), takes the
/// object by its identity, binds every value and bumps the catalog version in the same transaction.
/// </summary>
public sealed class ProductCatalogOwnerStore(GovernanceRepository repository) : IProductCatalogOwnerStore
{
    private readonly GovernanceRepository _repo = repository ?? throw new ArgumentNullException(nameof(repository));

    public Task SetOwnerAsync(TenantId tenant, TableIdentifier catalogObject, Sid? owner, CancellationToken ct = default) =>
        ProductCatalogOwnerOperations.SetOwnerAsync(_repo, tenant, catalogObject, owner, ct);
}

internal static class ProductCatalogOwnerOperations
{
    private const string TableLookupPgSql = "SELECT id FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)";
    private const string TableLookupSsSql = "SELECT id FROM TABLES WHERE source_name_lc = LOWER(@domain) AND schema_name_lc = LOWER(@schema) AND table_name_lc = LOWER(@table)";
    private const string FindOwnerSql = "SELECT id FROM DATA_OWNERS WHERE UPPER(ad_sid) = UPPER(@sid)";
    private const string InsertOwnerSql = "INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active) VALUES (@id, @sid, @sid, @sid, '', 1)";
    private const string DeleteTableOwnersSql = "DELETE FROM TABLE_OWNERS WHERE table_id = @tid";
    private const string InsertTableOwnerSql = "INSERT INTO TABLE_OWNERS (id, table_id, data_owner_id, owner_role) VALUES (@id, @tid, @oid, 'PRIMARY')";

    public static async Task SetOwnerAsync(GovernanceRepository repo, TenantId tenant, TableIdentifier catalogObject, Sid? owner, CancellationToken ct)
    {
        TenantScope.RequireTenant(tenant, GovernanceTable.TableOwners.ToSqlName());
        if (!ProductSource.TryParse(catalogObject, tenant, out _, out _))
        {
            throw new ArgumentException("The object is not a product object of the tenant.", nameof(catalogObject));
        }

        if (owner is { } sid && string.IsNullOrWhiteSpace(sid.Value))
        {
            throw new ArgumentException("The owner must not be blank.", nameof(owner));
        }

        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        Autheris.Application.Catalog.CatalogVersion version;
        try
        {
            var tableId = await FindTableAsync(repo, conn, tx, catalogObject, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The catalog object of the product is not persisted.");
            var ownerId = owner is { } o ? await EnsureOwnerAsync(repo, conn, tx, o.Value.Trim(), ct).ConfigureAwait(false) : null;
            await using (var delete = repo.CreateProductCommand(conn, tx, DeleteTableOwnersSql))
            {
                delete.AddBound("@tid", tableId);
                await repo.ExecuteNonQueryAsync(delete, "Governance.Products.ClearOwners", ct).ConfigureAwait(false);
            }

            if (ownerId is not null)
            {
                await using var insert = repo.CreateProductCommand(conn, tx, InsertTableOwnerSql);
                insert.AddBound("@id", Guid.NewGuid().ToString());
                insert.AddBound("@tid", tableId);
                insert.AddBound("@oid", ownerId);
                await repo.ExecuteNonQueryAsync(insert, "Governance.Products.SetOwner", ct).ConfigureAwait(false);
            }

            version = await repo.BumpCatalogVersionInternalAsync(conn, tx, 0, 1, ct).ConfigureAwait(false);
            await repo.CommitTransactionAsync(tx, ct).ConfigureAwait(false);
        }
        catch
        {
            await repo.RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }

        await repo.WriteInvalidator.InvalidateTablesAsync(tenant, [catalogObject], version, repo.InvalidateTableMetadata, ct).ConfigureAwait(false);
    }

    private static async Task<string?> FindTableAsync(GovernanceRepository repo, DbConnection conn, DbTransaction tx, TableIdentifier table, CancellationToken ct)
    {
        await using var cmd = repo.CreateProductCommand(conn, tx, repo.Dialect is PostgreSqlGovernanceDialect ? TableLookupPgSql : TableLookupSsSql);
        cmd.AddBound("@domain", table.Domain);
        cmd.AddBound("@schema", table.Schema);
        cmd.AddBound("@table", table.TableName);
        return (await repo.ExecuteScalarAsync(cmd, "Governance.Products.FindObject", ct).ConfigureAwait(false))?.ToString();
    }

    private static async Task<string> EnsureOwnerAsync(GovernanceRepository repo, DbConnection conn, DbTransaction tx, string sid, CancellationToken ct)
    {
        var existing = await FindOwnerAsync(repo, conn, tx, sid, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        await using var insert = repo.CreateProductCommand(conn, tx, InsertOwnerSql);
        insert.AddBound("@id", Guid.NewGuid().ToString());
        insert.AddBound("@sid", sid);
        await repo.ExecuteNonQueryAsync(insert, "Governance.Products.CreateOwner", ct).ConfigureAwait(false);
        return await FindOwnerAsync(repo, conn, tx, sid, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The data owner could not be created.");
    }

    private static async Task<string?> FindOwnerAsync(GovernanceRepository repo, DbConnection conn, DbTransaction tx, string sid, CancellationToken ct)
    {
        await using var cmd = repo.CreateProductCommand(conn, tx, FindOwnerSql);
        cmd.AddBound("@sid", sid);
        return (await repo.ExecuteScalarAsync(cmd, "Governance.Products.FindOwner", ct).ConfigureAwait(false))?.ToString();
    }
}
