namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public partial class GovernanceRepository : IClassificationRepository
{
    internal static class ClassificationAccessSql
    {
        public const string SelectByConsentSql = @"SELECT tenant_id, consent_id, code, access_level, valid_until, approved_by_sid, approved_at
FROM CONSENT_CLASSIFICATION_ACCESS
WHERE tenant_id = @tenant_id AND consent_id = @consent_id
ORDER BY code ASC";

        public const string SelectBatchPrefixWithTenant = @"SELECT tenant_id, consent_id, code, access_level, valid_until, approved_by_sid, approved_at
FROM CONSENT_CLASSIFICATION_ACCESS
WHERE tenant_id = @tenant_id AND consent_id IN (";

        public const string SelectBatchPrefixAll = @"SELECT tenant_id, consent_id, code, access_level, valid_until, approved_by_sid, approved_at
FROM CONSENT_CLASSIFICATION_ACCESS
WHERE consent_id IN (";

        public const string UpsertPgSql = @"INSERT INTO CONSENT_CLASSIFICATION_ACCESS
(tenant_id, consent_id, code, access_level, valid_until, approved_by_sid, approved_at)
VALUES
(@tenant_id, @consent_id, @code, @access_level, @valid_until, @approved_by_sid, @approved_at)
ON CONFLICT (tenant_id, consent_id, code) DO UPDATE SET
    access_level = EXCLUDED.access_level,
    valid_until = EXCLUDED.valid_until,
    approved_by_sid = EXCLUDED.approved_by_sid,
    approved_at = EXCLUDED.approved_at";

        public const string UpsertSsSql = @"MERGE CONSENT_CLASSIFICATION_ACCESS WITH (HOLDLOCK) AS t
USING (SELECT @tenant_id AS tenant_id, @consent_id AS consent_id, @code AS code) AS s
ON t.tenant_id = s.tenant_id AND t.consent_id = s.consent_id AND t.code = s.code
WHEN MATCHED THEN UPDATE SET
    access_level = @access_level,
    valid_until = @valid_until,
    approved_by_sid = @approved_by_sid,
    approved_at = @approved_at
WHEN NOT MATCHED THEN INSERT
    (tenant_id, consent_id, code, access_level, valid_until, approved_by_sid, approved_at)
VALUES
    (@tenant_id, @consent_id, @code, @access_level, @valid_until, @approved_by_sid, @approved_at);";
    }

    public async Task<IReadOnlyList<ConsentClassificationAccess>> GetConsentClassificationAccessAsync(
        string tenantId,
        Guid consentId,
        CancellationToken ct = default)
    {
        var validTenant = ClassificationScopes.ValidateScope(tenantId, allowGlobal: false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ClassificationAccessSql.SelectByConsentSql;
        cmd.AddBound("@tenant_id", validTenant);
        cmd.AddBound("@consent_id", consentId.ToString());

        var rows = new List<ConsentClassificationAccess>();
        await using var reader = await ExecuteReaderAsync(cmd, "Classifications.GetConsentAccess", ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(ReadConsentClassificationAccess(reader));
        }

        return rows;
    }

    public async Task<IReadOnlyList<ConsentClassificationAccess>> GetConsentClassificationAccessBatchAsync(
        string tenantId,
        IReadOnlyList<Guid> consentIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consentIds);
        var validTenant = ClassificationScopes.ValidateScope(tenantId, allowGlobal: false);
        if (consentIds.Count == 0)
        {
            return Array.Empty<ConsentClassificationAccess>();
        }

        var distinctIds = consentIds.Select(id => id.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rows = new List<ConsentClassificationAccess>();
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        for (int offset = 0; offset < distinctIds.Count; offset += ConsentSqlCore.ConsentHydrateBatchSize)
        {
            var batch = distinctIds.GetRange(offset, Math.Min(ConsentSqlCore.ConsentHydrateBatchSize, distinctIds.Count - offset));
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ClassificationAccessSql.SelectBatchPrefixWithTenant +
                ConsentSqlCore.BindIdInClause(cmd, "cid", batch) + ") ORDER BY consent_id ASC, code ASC";
            cmd.AddBound("@tenant_id", validTenant);

            await using var reader = await ExecuteReaderAsync(cmd, "Classifications.GetConsentAccessBatch", ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(ReadConsentClassificationAccess(reader));
            }
        }

        return rows;
    }

    public async Task<ConsentClassificationAccess> UpsertConsentClassificationAccessAsync(
        ConsentClassificationAccess entry,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var validTenant = ClassificationScopes.ValidateScope(entry.TenantId, allowGlobal: false);
        ClassificationsSql.ValidateClassificationCodeArg(entry.Code, nameof(entry));
        if (entry.ConsentId == Guid.Empty)
        {
            throw new ArgumentException("ConsentId must not be empty.", nameof(entry));
        }

        var level = Enum.IsDefined(entry.AccessLevel) ? entry.AccessLevel : ColumnAccessLevel.Deny;
        var approvedAt = entry.ApprovedAt == default ? TimeProvider.System.GetUtcNow() : entry.ApprovedAt;
        var normalized = entry with
        {
            TenantId = validTenant,
            AccessLevel = level,
            ApprovedAt = approvedAt.ToUniversalTime()
        };

        CatalogVersion? newVersion = null;
        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await UpsertConsentClassificationAccessInTxAsync(conn, tx, normalized, IsPostgreSql, ct).ConfigureAwait(false);
                newVersion = await BumpCatalogVersionInternalAsync(conn, tx, 0, 1, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        if (newVersion.HasValue)
        {
            await _writeInvalidator.InvalidateWideAsync(TryScopeTenant(normalized.TenantId), newVersion.Value, ct: ct).ConfigureAwait(false);
            await FlushScopePendingAsync(ct).ConfigureAwait(false);
        }

        return normalized;
    }

    internal static async Task HydrateConsentClassificationAccessBatchAsync(
        DbConnection conn,
        List<Consent> consents,
        CancellationToken ct)
    {
        if (consents.Count == 0)
        {
            return;
        }

        var consentIds = consents.Select(c => c.Id.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var map = consents.GroupBy(c => c.Id.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, _ => new List<ConsentClassificationAccess>(), StringComparer.OrdinalIgnoreCase);

        for (int offset = 0; offset < consentIds.Count; offset += ConsentSqlCore.ConsentHydrateBatchSize)
        {
            var batch = consentIds.GetRange(offset, Math.Min(ConsentSqlCore.ConsentHydrateBatchSize, consentIds.Count - offset));
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ClassificationAccessSql.SelectBatchPrefixAll +
                ConsentSqlCore.BindIdInClause(cmd, "cid", batch) + ") ORDER BY code ASC";

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = ReadConsentClassificationAccess(reader);
                if (map.TryGetValue(row.ConsentId.ToString(), out var list))
                {
                    list.Add(row);
                }
            }
        }

        foreach (var c in consents)
        {
            c.ClassificationAccess = map[c.Id.ToString()];
        }
    }

    internal static async Task InsertConsentClassificationAccessAsync(
        DbConnection conn,
        DbTransaction tx,
        Consent consent,
        CancellationToken ct)
    {
        if (consent.ClassificationAccess.Count == 0)
        {
            return;
        }

        var isPg = IsPostgreSqlConnection(conn);
        foreach (var raw in consent.ClassificationAccess)
        {
            ClassificationsSql.ValidateClassificationCodeArg(raw.Code, nameof(consent));
            var normalized = raw with
            {
                TenantId = string.IsNullOrWhiteSpace(raw.TenantId) ? consent.TenantId.Value : raw.TenantId,
                ConsentId = consent.Id,
                AccessLevel = Enum.IsDefined(raw.AccessLevel) ? raw.AccessLevel : ColumnAccessLevel.Deny,
                ApprovedAt = (raw.ApprovedAt == default ? consent.ValidFrom : raw.ApprovedAt).ToUniversalTime()
            };
            await UpsertConsentClassificationAccessInTxAsync(conn, tx, normalized, isPg, ct).ConfigureAwait(false);
        }
    }

    private static async Task UpsertConsentClassificationAccessInTxAsync(
        DbConnection conn,
        DbTransaction tx,
        ConsentClassificationAccess entry,
        bool isPostgreSql,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = isPostgreSql ? ClassificationAccessSql.UpsertPgSql : ClassificationAccessSql.UpsertSsSql;
        cmd.AddBound("@tenant_id", entry.TenantId);
        cmd.AddBound("@consent_id", entry.ConsentId.ToString());
        cmd.AddBound("@code", entry.Code);
        cmd.AddBound("@access_level", (short)(int)entry.AccessLevel, DbType.Int16);
        cmd.AddBound("@valid_until", entry.ValidUntil?.ToUniversalTime(), DbType.DateTimeOffset);
        cmd.AddBound("@approved_by_sid", entry.ApprovedBySid);
        cmd.AddBound("@approved_at", entry.ApprovedAt.ToUniversalTime(), DbType.DateTimeOffset);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static ConsentClassificationAccess ReadConsentClassificationAccess(DbDataReader reader)
    {
        var rawLevel = Convert.ToInt32(reader.GetValue(3));
        var parsed = (ColumnAccessLevel)rawLevel;
        return new ConsentClassificationAccess
        {
            TenantId = reader.GetString(0),
            ConsentId = Guid.Parse(reader.GetString(1)),
            Code = reader.GetString(2),
            AccessLevel = parsed is ColumnAccessLevel.Deny or ColumnAccessLevel.Mask or ColumnAccessLevel.Clear
                ? parsed
                : ColumnAccessLevel.Deny,
            ValidUntil = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            ApprovedBySid = reader.IsDBNull(5) ? null : reader.GetString(5),
            ApprovedAt = reader.GetFieldValue<DateTimeOffset>(6)
        };
    }

    private static bool IsPostgreSqlConnection(DbConnection conn) =>
        conn.GetType().FullName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
}
