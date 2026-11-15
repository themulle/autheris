namespace Autheris.Infrastructure.Persistence.Docs;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Docs.Interfaces;
using Autheris.Infrastructure.Persistence.Dialects;

internal static class DocumentStoreOperations
{
    public static Task<DocWriteOutcome> CreateManyAsync(GovernanceRepository repo, IReadOnlyList<DocRecord> records, DocQuota quota, CancellationToken ct)
    {
        if (records.Count == 0) return Task.FromResult(new DocWriteOutcome(DocWriteStatus.Created));
        var now = TimeProvider.System.GetUtcNow();
        return RunInTxAsync(repo, async (conn, tx) =>
        {
            DocRecord? last = null;
            foreach (var raw in records)
            {
                var updatedAt = (raw.UpdatedAt == default ? now : raw.UpdatedAt).ToUniversalTime();
                var doc = raw with { TenantId = NormalizeScope(raw.TenantId), UpdatedAt = updatedAt, DeletedAt = null };
                if (doc.TenantId is not null && await OfficialActiveExistsAsync(repo, conn, tx, doc, ct).ConfigureAwait(false))
                    return (false, new DocWriteOutcome(DocWriteStatus.ReservedKey));
                var version = await TryInsertOrReviveAsync(repo, conn, tx, doc, updatedAt, ct).ConfigureAwait(false);
                if (!version.HasValue) return (false, new DocWriteOutcome(DocWriteStatus.AlreadyExists));
                if (doc.TenantId is not null && !await TryReserveQuotaAsync(repo, conn, tx, doc.TenantId, 1, doc.ByteCount, quota, updatedAt, ct).ConfigureAwait(false))
                    return (false, new DocWriteOutcome(DocWriteStatus.QuotaExceeded));
                last = doc with { Version = version.Value };
            }
            return (true, new DocWriteOutcome(DocWriteStatus.Created, last));
        }, ct);
    }

    public static Task<DocWriteOutcome> ReplaceAsync(GovernanceRepository repo, DocRecord record, long? expectedVersion, DocQuota quota, CancellationToken ct)
    {
        var tenantId = NormalizeScope(record.TenantId);
        var updatedAt = (record.UpdatedAt == default ? TimeProvider.System.GetUtcNow() : record.UpdatedAt).ToUniversalTime();
        var doc = record with { TenantId = tenantId, UpdatedAt = updatedAt, DeletedAt = null };
        return RunInTxAsync(repo, async (conn, tx) =>
        {
            var cur = await LockRowAsync(repo, conn, tx, new DocKey(tenantId, doc.AppId, doc.Category, doc.DocId), ct).ConfigureAwait(false);
            if (cur is null || cur.Value.Deleted) return (false, new DocWriteOutcome(DocWriteStatus.NotFound));
            if (expectedVersion.HasValue && cur.Value.Version != expectedVersion.Value) return (false, new DocWriteOutcome(DocWriteStatus.VersionMismatch));
            if (tenantId is not null && !await TryReserveQuotaAsync(repo, conn, tx, tenantId, 0, (long)doc.ByteCount - cur.Value.ByteCount, quota, updatedAt, ct).ConfigureAwait(false))
                return (false, new DocWriteOutcome(DocWriteStatus.QuotaExceeded));
            await using var cmd = repo.CreateDocCommand(conn, tx, tenantId is null ? DocumentStoreSql.UpdateOfficialSql : DocumentStoreSql.UpdateTenantSql);
            DocumentStoreSql.BindPayload(cmd, doc, updatedAt);
            await repo.ExecuteNonQueryAsync(cmd, "Governance.Docs.Replace", ct).ConfigureAwait(false);
            return (true, new DocWriteOutcome(DocWriteStatus.Replaced, doc with { Version = cur.Value.Version + 1 }));
        }, ct);
    }

    public static Task<DocWriteOutcome> DeleteAsync(GovernanceRepository repo, DocKey key, long? expectedVersion, DateTimeOffset deletedAt, CancellationToken ct)
    {
        var tenantId = NormalizeScope(key.TenantId);
        var at = (deletedAt == default ? TimeProvider.System.GetUtcNow() : deletedAt).ToUniversalTime();
        return RunInTxAsync(repo, async (conn, tx) =>
        {
            var cur = await LockRowAsync(repo, conn, tx, key with { TenantId = tenantId }, ct).ConfigureAwait(false);
            if (cur is null || cur.Value.Deleted) return (false, new DocWriteOutcome(DocWriteStatus.NotFound));
            if (expectedVersion.HasValue && cur.Value.Version != expectedVersion.Value) return (false, new DocWriteOutcome(DocWriteStatus.VersionMismatch));
            await using var cmd = repo.CreateDocCommand(conn, tx, tenantId is null ? DocumentStoreSql.SoftDeleteOfficialSql : DocumentStoreSql.SoftDeleteTenantSql);
            DocumentStoreSql.BindKey(cmd, tenantId, key.AppId, key.Category, key.DocId);
            cmd.AddBound("@updated_at", at, DbType.DateTimeOffset);
            await repo.ExecuteNonQueryAsync(cmd, "Governance.Docs.Delete", ct).ConfigureAwait(false);
            if (tenantId is not null)
                await ExecBoundAsync(repo, conn, tx, DocumentStoreSql.DecrementQuotaSql, "Governance.Docs.DecrementQuota", "@tenant_id", tenantId, null, ct,
                    ("@old_bytes", (long)cur.Value.ByteCount, DbType.Int64), ("@updated_at", at, DbType.DateTimeOffset)).ConfigureAwait(false);
            return (true, new DocWriteOutcome(DocWriteStatus.Deleted));
        }, ct);
    }

    public static Task<int> PurgeTenantAsync(GovernanceRepository repo, string tenantId, CancellationToken ct)
    {
        var validTenant = TenantScope.RequireTenantString(tenantId, GovernanceTable.DocTenantDocuments.ToSqlName());
        return RunInTxAsync(repo, async (conn, tx) =>
        {
            var deleted = await ExecBoundAsync(repo, conn, tx, DocumentStoreSql.PurgeTenantDocsSql, "Governance.Docs.PurgeTenantDocs", "@tenant_id", validTenant, null, ct).ConfigureAwait(false);
            await ExecBoundAsync(repo, conn, tx, DocumentStoreSql.PurgeTenantUsageSql, "Governance.Docs.PurgeTenantUsage", "@tenant_id", validTenant, null, ct).ConfigureAwait(false);
            return (true, deleted);
        }, ct);
    }

    public static async Task<DocRecord?> GetAsync(GovernanceRepository repo, DocKey key, CancellationToken ct)
    {
        var tenantId = NormalizeScope(key.TenantId);
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = repo.CreateDocCommand(conn, null, tenantId is null ? DocumentStoreSql.GetOfficialSql : DocumentStoreSql.GetTenantSql);
        DocumentStoreSql.BindKey(cmd, tenantId, key.AppId, key.Category, key.DocId);
        await using var r = await repo.ExecuteReaderAsync(cmd, "Governance.Docs.Get", ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? DocumentStoreSql.ReadDoc(r, tenantId, 0) : null;
    }

    public static async Task<IReadOnlyList<DocRecord>> ReadChangedSinceAsync(GovernanceRepository repo, DateTimeOffset? since, CancellationToken ct)
    {
        var isPg = repo.Dialect is PostgreSqlGovernanceDialect;
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = repo.CreateDocCommand(conn, null, since.HasValue
            ? (isPg ? DocumentStoreSql.ReadChangedSincePgSql : DocumentStoreSql.ReadChangedSinceSsSql)
            : (isPg ? DocumentStoreSql.ReadAllChangedPgSql : DocumentStoreSql.ReadAllChangedSsSql));
        if (since.HasValue) cmd.AddBound("@since", since.Value.ToUniversalTime(), DbType.DateTimeOffset);
        await using var r = await repo.ExecuteReaderAsync(cmd, "Governance.Docs.ReadChanged", ct).ConfigureAwait(false);
        var items = new List<DocRecord>();
        while (await r.ReadAsync(ct).ConfigureAwait(false)) items.Add(DocumentStoreSql.ReadDoc(r, r.IsDBNull(0) ? null : r.GetString(0), 1));
        return items;
    }

    public static Task<int> PurgeTombstonesAsync(GovernanceRepository repo, DateTimeOffset cutoff, CancellationToken ct)
    {
        var utcCutoff = cutoff.ToUniversalTime();
        return RunInTxAsync(repo, async (conn, tx) =>
        {
            var n1 = await ExecBoundAsync(repo, conn, tx, DocumentStoreSql.PurgeOfficialTombstonesSql, "Governance.Docs.PurgeOfficialTombstones", "@cutoff", utcCutoff, DbType.DateTimeOffset, ct).ConfigureAwait(false);
            var n2 = await ExecBoundAsync(repo, conn, tx, DocumentStoreSql.PurgeTenantTombstonesSql, "Governance.Docs.PurgeTenantTombstones", "@cutoff", utcCutoff, DbType.DateTimeOffset, ct).ConfigureAwait(false);
            return (true, n1 + n2);
        }, ct);
    }

    public static async Task<DocTenantUsage> GetTenantUsageAsync(GovernanceRepository repo, string tenantId, CancellationToken ct)
    {
        var validTenant = TenantScope.RequireTenantString(tenantId, GovernanceTable.DocTenantUsage.ToSqlName());
        await using var conn = await repo.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = repo.CreateDocCommand(conn, null, DocumentStoreSql.GetUsageSql);
        cmd.AddBound("@tenant_id", validTenant);
        await using var r = await repo.ExecuteReaderAsync(cmd, "Governance.Docs.GetUsage", ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? new DocTenantUsage(validTenant, r.GetInt32(0), r.GetInt64(1), r.GetFieldValue<DateTimeOffset>(2)) : new DocTenantUsage(validTenant, 0, 0, DateTimeOffset.UnixEpoch);
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
        catch { await repo.RollbackTransactionAsync(tx, ct).ConfigureAwait(false); throw; }
    }

    private static string? NormalizeScope(string? tenantId) => tenantId is null ? null : TenantScope.RequireTenantString(tenantId, GovernanceTable.DocTenantDocuments.ToSqlName());

    private static async Task<bool> OfficialActiveExistsAsync(GovernanceRepository repo, DbConnection conn, DbTransaction tx, DocRecord doc, CancellationToken ct)
    {
        await using var cmd = repo.CreateDocCommand(conn, tx, DocumentStoreSql.CheckOfficialActiveSql);
        DocumentStoreSql.BindKey(cmd, null, doc.AppId, doc.Category, doc.DocId);
        return await repo.ExecuteScalarAsync(cmd, "Governance.Docs.CheckOfficial", ct).ConfigureAwait(false) is not null;
    }

    private static async Task<long?> TryInsertOrReviveAsync(GovernanceRepository repo, DbConnection conn, DbTransaction tx, DocRecord doc, DateTimeOffset updatedAt, CancellationToken ct)
    {
        var isPg = repo.Dialect is PostgreSqlGovernanceDialect;
        var sql = doc.TenantId is null ? (isPg ? DocumentStoreSql.CreateOfficialPgSql : DocumentStoreSql.CreateOfficialSsSql) : (isPg ? DocumentStoreSql.CreateTenantPgSql : DocumentStoreSql.CreateTenantSsSql);
        await using var cmd = repo.CreateDocCommand(conn, tx, sql);
        DocumentStoreSql.BindPayload(cmd, doc, updatedAt);
        await using var r = await repo.ExecuteReaderAsync(cmd, "Governance.Docs.Create", ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? r.GetInt64(0) : null;
    }

    private static async Task<bool> TryReserveQuotaAsync(GovernanceRepository repo, DbConnection conn, DbTransaction tx, string tenantId, int deltaDocs, long deltaBytes, DocQuota quota, DateTimeOffset updatedAt, CancellationToken ct)
    {
        var ensureSql = repo.Dialect is PostgreSqlGovernanceDialect ? DocumentStoreSql.EnsureUsagePgSql : DocumentStoreSql.EnsureUsageSsSql;
        await ExecBoundAsync(repo, conn, tx, ensureSql, "Governance.Docs.EnsureUsage", "@tenant_id", tenantId, null, ct, ("@updated_at", updatedAt, DbType.DateTimeOffset)).ConfigureAwait(false);
        return await ExecBoundAsync(repo, conn, tx, DocumentStoreSql.ReserveQuotaSql, "Governance.Docs.ReserveQuota", "@tenant_id", tenantId, null, ct,
            ("@delta_docs", deltaDocs, DbType.Int32), ("@delta_bytes", deltaBytes, DbType.Int64), ("@max_docs", quota.MaxDocumentsPerTenant, DbType.Int32), ("@max_bytes", quota.MaxBytesPerTenant, DbType.Int64), ("@updated_at", updatedAt, DbType.DateTimeOffset)).ConfigureAwait(false) == 1;
    }

    private static async Task<int> ExecBoundAsync(GovernanceRepository repo, DbConnection conn, DbTransaction tx, string sql, string id, string param, object val, DbType? dbType, CancellationToken ct, params (string Name, object Val, DbType Type)[] extra)
    {
        await using var cmd = repo.CreateDocCommand(conn, tx, sql);
        cmd.AddBound(param, val, dbType);
        foreach (var (n, v, t) in extra) cmd.AddBound(n, v, t);
        return await repo.ExecuteNonQueryAsync(cmd, id, ct).ConfigureAwait(false);
    }

    private static async Task<(int ByteCount, long Version, bool Deleted)?> LockRowAsync(GovernanceRepository repo, DbConnection conn, DbTransaction tx, DocKey key, CancellationToken ct)
    {
        var isPg = repo.Dialect is PostgreSqlGovernanceDialect;
        await using var cmd = repo.CreateDocCommand(conn, tx, key.TenantId is null
            ? (isPg ? DocumentStoreSql.LockOfficialPgSql : DocumentStoreSql.LockOfficialSsSql)
            : (isPg ? DocumentStoreSql.LockTenantPgSql : DocumentStoreSql.LockTenantSsSql));
        DocumentStoreSql.BindKey(cmd, key.TenantId, key.AppId, key.Category, key.DocId);
        await using var r = await repo.ExecuteReaderAsync(cmd, "Governance.Docs.Lock", ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? (r.GetInt32(0), r.GetInt64(1), !r.IsDBNull(2)) : null;
    }
}
