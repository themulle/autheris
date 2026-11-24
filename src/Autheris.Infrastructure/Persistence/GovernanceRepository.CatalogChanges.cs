namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.External;
using Autheris.Domain.Common;
using Autheris.Infrastructure.Persistence.Dialects;

public partial class GovernanceRepository : ICatalogChangeStore
{
    public async Task<IReadOnlyDictionary<string, CatalogObjectVersionRow>> GetObjectVersionsAsync(
        TenantId tenantId,
        CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.CatalogObjectVersions));
        const string selectSql = @"SELECT object_id, content_hash, seq, removed
FROM CATALOG_OBJECT_VERSIONS
WHERE tenant_id = @tenantId";

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = selectSql;
        cmd.AddBound("@tenantId", tenantId.Value);

        var rows = new Dictionary<string, CatalogObjectVersionRow>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var objectId = reader.GetString(0);
            var contentHash = reader.GetString(1).Trim();
            var seq = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
            var removed = Convert.ToBoolean(reader.GetValue(3), CultureInfo.InvariantCulture);
            rows[objectId] = new CatalogObjectVersionRow(objectId, contentHash, seq, removed);
        }

        return rows;
    }

    public async Task<long> ApplyVersionChangesAsync(
        TenantId tenantId,
        IReadOnlyList<CatalogVersionUpsert> upserts,
        DateTimeOffset changedAt,
        CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.CatalogChanges));
        StoredTenant.RequireWritable(tenantId);
        ArgumentNullException.ThrowIfNull(upserts);

        if (upserts.Count == 0)
        {
            return 0;
        }

        const string maxSeqSql = @"SELECT COALESCE(MAX(seq), 0)
FROM CATALOG_CHANGES
WHERE tenant_id = @tenantId";

        const string insertChangeSql = @"INSERT INTO CATALOG_CHANGES (tenant_id, seq, object_id, kind, changed_at)
VALUES (@tenantId, @seq, @objectId, @kind, @changedAt)";

        const string updateVersionSql = @"UPDATE CATALOG_OBJECT_VERSIONS
SET content_hash = @contentHash, seq = @seq, removed = @removed
WHERE tenant_id = @tenantId AND object_id = @objectId";

        const string insertVersionSql = @"INSERT INTO CATALOG_OBJECT_VERSIONS (tenant_id, object_id, content_hash, seq, removed)
VALUES (@tenantId, @objectId, @contentHash, @seq, @removed)";

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            long currentSeq;
            await using (var maxCmd = conn.CreateCommand())
            {
                maxCmd.Transaction = tx;
                maxCmd.CommandText = maxSeqSql;
                maxCmd.AddBound("@tenantId", tenantId.Value);
                var raw = await maxCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                currentSeq = Convert.ToInt64(raw ?? 0L, CultureInfo.InvariantCulture);
            }

            foreach (var item in upserts)
            {
                currentSeq++;
                var kind = item.Removed ? "REMOVED" : "UPSERT";

                await using (var insChange = conn.CreateCommand())
                {
                    insChange.Transaction = tx;
                    insChange.CommandText = insertChangeSql;
                    insChange.AddBound("@tenantId", tenantId.Value);
                    insChange.AddBound("@seq", currentSeq, DbType.Int64);
                    insChange.AddBound("@objectId", item.ObjectId);
                    insChange.AddBound("@kind", kind);
                    insChange.AddBound("@changedAt", changedAt, DbType.DateTimeOffset);
                    await insChange.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                int updated;
                await using (var updVer = conn.CreateCommand())
                {
                    updVer.Transaction = tx;
                    updVer.CommandText = updateVersionSql;
                    updVer.AddBound("@contentHash", item.ContentHash);
                    updVer.AddBound("@seq", currentSeq, DbType.Int64);
                    updVer.AddBound("@removed", item.Removed, DbType.Boolean);
                    updVer.AddBound("@tenantId", tenantId.Value);
                    updVer.AddBound("@objectId", item.ObjectId);
                    updated = await updVer.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                if (updated == 0)
                {
                    await using var insVer = conn.CreateCommand();
                    insVer.Transaction = tx;
                    insVer.CommandText = insertVersionSql;
                    insVer.AddBound("@tenantId", tenantId.Value);
                    insVer.AddBound("@objectId", item.ObjectId);
                    insVer.AddBound("@contentHash", item.ContentHash);
                    insVer.AddBound("@seq", currentSeq, DbType.Int64);
                    insVer.AddBound("@removed", item.Removed, DbType.Boolean);
                    await insVer.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            return currentSeq;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<CatalogChangesFeedResult> ReadCatalogChangesAsync(
        TenantId tenantId,
        long sinceSeq,
        int limit,
        CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.CatalogChanges));
        int clampedLimit = Math.Clamp(limit, 1, 500);
        long normalizedSince = Math.Max(0L, sinceSeq);

        const string selectPgSql = @"SELECT object_id, kind, seq, changed_at
FROM CATALOG_CHANGES
WHERE tenant_id = @tenantId AND seq > @sinceSeq
ORDER BY seq ASC
LIMIT @limit";

        const string selectSqlServerSql = @"SELECT TOP (@limit) object_id, kind, seq, changed_at
FROM CATALOG_CHANGES
WHERE tenant_id = @tenantId AND seq > @sinceSeq
ORDER BY seq ASC";

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = _dialect is SqlServerGovernanceDialect ? selectSqlServerSql : selectPgSql;
        cmd.AddBound("@tenantId", tenantId.Value);
        cmd.AddBound("@sinceSeq", normalizedSince, DbType.Int64);
        cmd.AddBound("@limit", clampedLimit, DbType.Int32);

        var items = new List<CatalogChangeRecord>();
        long nextSince = normalizedSince;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var objectId = reader.GetString(0);
            var kind = reader.GetString(1);
            var seq = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
            var changedAt = ReadCatalogChangeTimestamp(reader, 3);
            items.Add(new CatalogChangeRecord(objectId, kind, seq, changedAt));
            if (seq > nextSince)
            {
                nextSince = seq;
            }
        }

        return new CatalogChangesFeedResult(items, nextSince);
    }

    public async Task<IReadOnlyList<TenantId>> ListKnownTenantIdsAsync(CancellationToken ct = default)
    {
        const string selectTenantsSql = @"SELECT tenant_id FROM TENANT_REGISTRY WHERE collided = 0
UNION SELECT DISTINCT tenant_id FROM CATALOG_OBJECT_VERSIONS
UNION SELECT DISTINCT tenant_id FROM GOVERNANCE_DOMAINS UNION SELECT DISTINCT tenant_id FROM DATA_PRODUCTS";

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = selectTenantsSql;

        var tenants = new List<TenantId>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            var raw = reader.GetString(0);
            if (TenantId.TryParse(raw, out var tenant) && seen.Add(tenant.Value!))
            {
                tenants.Add(tenant);
            }
        }

        return tenants;
    }

    private static DateTimeOffset ReadCatalogChangeTimestamp(DbDataReader reader, int ordinal)
    {
        var raw = reader.GetValue(ordinal);
        return raw switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => DateTimeOffset.Parse(Convert.ToString(raw, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
        };
    }
}
