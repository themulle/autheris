using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// Dialect-neutral persistence store for container-scoped <see cref="CatalogVisibilityGrant"/> records.
/// Always requires a validated tenant id and never writes into <c>CONSENTS</c>.
/// </summary>
public sealed class CatalogVisibilityGrantStore(
    IGovernanceSqlDialect dialect,
    TimeProvider? timeProvider = null,
    IGovernanceChangeNotifier? changeNotifier = null) : ICatalogVisibilityGrantRepository
{
    private const int SubjectBatchSize = 100;

    private const string PgTable = "catalog_visibility_grants";
    private const string SsTable = "dbo.CATALOG_VISIBILITY_GRANTS";

    private const string InsertPgSql =
        "INSERT INTO " + PgTable + " (id, tenant_id, container_kind, container_id, grantee_type, grantee_sid, grantee_sid_norm, valid_from, valid_to, granted_by_sid, approver_capacity, request_id, is_revoked, revoked_by_sid, revoked_at, revoke_reason, created_at) " +
        "VALUES (@id, @tenantId, @containerKind, @containerId, @granteeType, @granteeSid, @granteeSidNorm, @validFrom, @validTo, @grantedBySid, @approverCapacity, @requestId, FALSE, NULL, NULL, NULL, @createdAt)";

    private const string InsertSsSql =
        "INSERT INTO " + SsTable + " (id, tenant_id, container_kind, container_id, grantee_type, grantee_sid, grantee_sid_norm, valid_from, valid_to, granted_by_sid, approver_capacity, request_id, is_revoked, revoked_by_sid, revoked_at, revoke_reason, created_at) " +
        "VALUES (@id, @tenantId, @containerKind, @containerId, @granteeType, @granteeSid, @granteeSidNorm, @validFrom, @validTo, @grantedBySid, @approverCapacity, @requestId, 0, NULL, NULL, NULL, @createdAt)";

    private const string SelectColumns =
        "SELECT id, tenant_id, container_kind, container_id, grantee_type, grantee_sid, valid_from, valid_to, granted_by_sid, approver_capacity, request_id, is_revoked, revoked_by_sid, revoked_at, revoke_reason, created_at FROM ";

    private const string GetByIdPgSql = SelectColumns + PgTable + " WHERE tenant_id = @tenantId AND id = @id";
    private const string GetByIdSsSql = SelectColumns + SsTable + " WHERE tenant_id = @tenantId AND id = @id";

    private const string RevokePgSql =
        "UPDATE " + PgTable + " SET is_revoked = TRUE, revoked_by_sid = @revokedBy, revoked_at = @revokedAt, revoke_reason = @reason " +
        "WHERE tenant_id = @tenantId AND id = @id AND is_revoked = FALSE";

    private const string RevokeSsSql =
        "UPDATE " + SsTable + " SET is_revoked = 1, revoked_by_sid = @revokedBy, revoked_at = @revokedAt, revoke_reason = @reason " +
        "WHERE tenant_id = @tenantId AND id = @id AND is_revoked = 0";

    private const string RevokeForSubjectsPrefixPgSql =
        "UPDATE " + PgTable + " SET is_revoked = TRUE, revoked_by_sid = @revokedBy, revoked_at = @revokedAt, revoke_reason = @reason " +
        "WHERE tenant_id = @tenantId AND is_revoked = FALSE AND grantee_sid_norm IN (";

    private const string RevokeForSubjectsPrefixSsSql =
        "UPDATE " + SsTable + " SET is_revoked = 1, revoked_by_sid = @revokedBy, revoked_at = @revokedAt, revoke_reason = @reason " +
        "WHERE tenant_id = @tenantId AND is_revoked = 0 AND grantee_sid_norm IN (";

    private const string ListActivePrefixPgSql =
        SelectColumns + PgTable + " WHERE tenant_id = @tenantId AND is_revoked = FALSE AND valid_from <= @atTime AND (valid_to IS NULL OR valid_to > @atTime) AND grantee_sid_norm IN (";

    private const string ListActivePrefixSsSql =
        SelectColumns + SsTable + " WHERE tenant_id = @tenantId AND is_revoked = 0 AND valid_from <= @atTime AND (valid_to IS NULL OR valid_to > @atTime) AND grantee_sid_norm IN (";

    private const string ListByContainerPgSql =
        SelectColumns + PgTable + " WHERE tenant_id = @tenantId AND container_kind = @containerKind AND container_id = @containerId";

    private const string ListByContainerSsSql =
        SelectColumns + SsTable + " WHERE tenant_id = @tenantId AND container_kind = @containerKind AND container_id = @containerId";

    private readonly IGovernanceSqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly IGovernanceChangeNotifier? _changeNotifier = changeNotifier;
    private bool IsSqlServer => _dialect is SqlServerGovernanceDialect;

    public async Task<CatalogVisibilityGrant> CreateAsync(
        CatalogVisibilityGrant grant,
        DbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var tenantIdStr = RequireTenant(grant.TenantId);
        if (grant.ContainerId == Guid.Empty)
        {
            throw new ArgumentException("ContainerId is required for a catalog visibility grant.", nameof(grant));
        }

        if (grant.ValidTo.HasValue && grant.ValidTo.Value <= grant.ValidFrom)
        {
            throw new ArgumentException("ValidTo must be strictly greater than ValidFrom.", nameof(grant));
        }

        var id = grant.Id == Guid.Empty ? Guid.NewGuid() : grant.Id;
        var createdAt = (grant.CreatedAt == default ? _time.GetUtcNow() : grant.CreatedAt).ToUniversalTime();
        var validFrom = grant.ValidFrom.ToUniversalTime();
        var validTo = grant.ValidTo?.ToUniversalTime();

        var persisted = new CatalogVisibilityGrant
        {
            Id = id,
            TenantId = new TenantId(tenantIdStr),
            ContainerKind = grant.ContainerKind,
            ContainerId = grant.ContainerId,
            GranteeType = grant.GranteeType,
            GranteeSid = grant.GranteeSid,
            ValidFrom = validFrom,
            ValidTo = validTo,
            GrantedBySid = grant.GrantedBySid,
            ApproverCapacity = grant.ApproverCapacity,
            RequestId = grant.RequestId,
            IsRevoked = false,
            CreatedAt = createdAt
        };

        var epoch = await ExecuteWriteAsync(transaction, async (conn, tx) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = IsSqlServer ? InsertSsSql : InsertPgSql;
            cmd.AddBound("@id", persisted.Id.ToString("D"));
            cmd.AddBound("@tenantId", tenantIdStr);
            cmd.AddBound("@containerKind", persisted.ContainerKind.ToSqlString());
            cmd.AddBound("@containerId", persisted.ContainerId.ToString("D"));
            cmd.AddBound("@granteeType", persisted.GranteeType.ToString());
            cmd.AddBound("@granteeSid", persisted.GranteeSid.Value);
            cmd.AddBound("@granteeSidNorm", ConsentSubjectFilter.NormalizeParameter(persisted.GranteeSid.Value));
            cmd.AddBound("@validFrom", validFrom, DbType.DateTimeOffset);
            cmd.AddBound("@validTo", validTo.HasValue ? validTo.Value : DBNull.Value, DbType.DateTimeOffset);
            cmd.AddBound("@grantedBySid", persisted.GrantedBySid.Value);
            cmd.AddBound("@approverCapacity", persisted.ApproverCapacity.ToSqlString());
            cmd.AddBound("@requestId", persisted.RequestId.HasValue ? persisted.RequestId.Value.ToString("D") : DBNull.Value);
            cmd.AddBound("@createdAt", createdAt, DbType.DateTimeOffset);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return await BumpEpochInTxAsync(conn, tx, tenantIdStr, createdAt, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (transaction is null && _changeNotifier is not null)
        {
            await _changeNotifier.NotifyAsync(new GovernanceChangeEvent(tenantIdStr, epoch, null, createdAt), ct).ConfigureAwait(false);
        }

        return persisted;
    }

    public async Task<CatalogVisibilityGrant?> GetByIdAsync(TenantId tenantId, Guid grantId, CancellationToken ct = default)
    {
        var tenantIdStr = RequireTenant(tenantId);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IsSqlServer ? GetByIdSsSql : GetByIdPgSql;
        cmd.AddBound("@tenantId", tenantIdStr);
        cmd.AddBound("@id", grantId.ToString("D"));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadRow(reader) : null;
    }

    public async Task<bool> RevokeAsync(
        TenantId tenantId,
        Guid grantId,
        Sid revokedBySid,
        string reason,
        DateTimeOffset revokedAt,
        DbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        var tenantIdStr = RequireTenant(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var now = revokedAt.ToUniversalTime();
        var (revoked, epoch) = await ExecuteWriteAsync(transaction, async (conn, tx) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = IsSqlServer ? RevokeSsSql : RevokePgSql;
            cmd.AddBound("@tenantId", tenantIdStr);
            cmd.AddBound("@id", grantId.ToString("D"));
            cmd.AddBound("@revokedBy", revokedBySid.Value);
            cmd.AddBound("@revokedAt", now, DbType.DateTimeOffset);
            cmd.AddBound("@reason", reason.Trim());
            var count = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (count <= 0)
            {
                return (false, 0L);
            }

            var bumped = await BumpEpochInTxAsync(conn, tx, tenantIdStr, now, ct).ConfigureAwait(false);
            return (true, bumped);
        }, ct).ConfigureAwait(false);

        if (revoked && transaction is null && _changeNotifier is not null)
        {
            await _changeNotifier.NotifyAsync(new GovernanceChangeEvent(tenantIdStr, epoch, null, now), ct).ConfigureAwait(false);
        }

        return revoked;
    }

    public async Task<int> RevokeAllForSubjectAsync(
        TenantId tenantId,
        IReadOnlyCollection<Sid> subjects,
        Sid revokedBySid,
        string reason,
        DateTimeOffset revokedAt,
        DbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        var tenantIdStr = RequireTenant(tenantId);
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var normalized = ConsentSubjectFilter.DistinctNormalized(subjects).ToList();
        if (normalized.Count == 0)
        {
            return 0;
        }

        var now = revokedAt.ToUniversalTime();
        var (totalRevoked, epoch) = await ExecuteWriteAsync(transaction, async (conn, tx) =>
        {
            var count = 0;
            for (var offset = 0; offset < normalized.Count; offset += SubjectBatchSize)
            {
                var batch = normalized.Skip(offset).Take(SubjectBatchSize).ToList();
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                var inClause = BindSubjectInClause(cmd, batch);
                cmd.CommandText = (IsSqlServer ? RevokeForSubjectsPrefixSsSql : RevokeForSubjectsPrefixPgSql) + inClause + ")";
                cmd.AddBound("@tenantId", tenantIdStr);
                cmd.AddBound("@revokedBy", revokedBySid.Value);
                cmd.AddBound("@revokedAt", now, DbType.DateTimeOffset);
                cmd.AddBound("@reason", reason.Trim());
                count += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var bumped = count > 0 ? await BumpEpochInTxAsync(conn, tx, tenantIdStr, now, ct).ConfigureAwait(false) : 0L;
            return (count, bumped);
        }, ct).ConfigureAwait(false);

        if (totalRevoked > 0 && transaction is null && _changeNotifier is not null)
        {
            await _changeNotifier.NotifyAsync(new GovernanceChangeEvent(tenantIdStr, epoch, null, now), ct).ConfigureAwait(false);
        }

        return totalRevoked;
    }

    public async Task<IReadOnlyList<CatalogVisibilityGrant>> ListActiveForSubjectsAsync(
        TenantId tenantId,
        IReadOnlyCollection<Sid> subjects,
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        var tenantIdStr = RequireTenant(tenantId);
        ArgumentNullException.ThrowIfNull(subjects);
        var normalized = ConsentSubjectFilter.DistinctNormalized(subjects).ToList();
        if (normalized.Count == 0)
        {
            return [];
        }

        var utcAt = atTime.ToUniversalTime();
        var results = new List<CatalogVisibilityGrant>();
        var seen = new HashSet<Guid>();
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        for (var offset = 0; offset < normalized.Count; offset += SubjectBatchSize)
        {
            var batch = normalized.Skip(offset).Take(SubjectBatchSize).ToList();
            await using var cmd = conn.CreateCommand();
            var inClause = BindSubjectInClause(cmd, batch);
            cmd.CommandText = (IsSqlServer ? ListActivePrefixSsSql : ListActivePrefixPgSql) + inClause + ") ORDER BY created_at DESC, id";
            cmd.AddBound("@tenantId", tenantIdStr);
            cmd.AddBound("@atTime", utcAt, DbType.DateTimeOffset);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = ReadRow(reader);
                if (row.IsActive(utcAt) && seen.Add(row.Id))
                {
                    results.Add(row);
                }
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<CatalogVisibilityGrant>> ListByContainerAsync(
        TenantId tenantId,
        ContainerKind containerKind,
        Guid containerId,
        bool includeInactive = false,
        CancellationToken ct = default)
    {
        var tenantIdStr = RequireTenant(tenantId);
        if (containerId == Guid.Empty)
        {
            return [];
        }

        var now = _time.GetUtcNow();
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        var sql = IsSqlServer ? ListByContainerSsSql : ListByContainerPgSql;
        if (!includeInactive)
        {
            sql += IsSqlServer ? " AND is_revoked = 0" : " AND is_revoked = FALSE";
        }

        cmd.CommandText = sql + " ORDER BY created_at DESC, id";
        cmd.AddBound("@tenantId", tenantIdStr);
        cmd.AddBound("@containerKind", containerKind.ToSqlString());
        cmd.AddBound("@containerId", containerId.ToString("D"));

        var list = new List<CatalogVisibilityGrant>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = ReadRow(reader);
            if (includeInactive || row.IsActive(now))
            {
                list.Add(row);
            }
        }

        return list;
    }

    private static string BindSubjectInClause(DbCommand cmd, IReadOnlyList<string> batch)
    {
        var names = new string[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            names[i] = "@s" + i.ToString(CultureInfo.InvariantCulture);
            cmd.AddBound(names[i], batch[i]);
        }

        return string.Join(",", names);
    }

    private static CatalogVisibilityGrant ReadRow(DbDataReader reader)
    {
        var kindRaw = reader.GetString(2);
        var kind = ConsentWorkflowSqlMapping.TryParseContainerKind(kindRaw, out var parsedKind)
            ? parsedKind
            : ContainerKind.Domain;
        var capRaw = reader.GetString(9);
        var capacity = ConsentWorkflowSqlMapping.TryParseApproverCapacity(capRaw, out var parsedCap)
            ? parsedCap
            : ApproverCapacity.DataOwner;

        return new CatalogVisibilityGrant
        {
            Id = Guid.Parse(reader.GetString(0)),
            TenantId = StoredTenant.Parse(reader.GetString(1)),
            ContainerKind = kind,
            ContainerId = Guid.Parse(reader.GetString(3)),
            GranteeType = GranteeTypeParsing.ParseStored(reader.GetString(4)),
            GranteeSid = new Sid(reader.GetString(5)),
            ValidFrom = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 6) ?? DateTimeOffset.MinValue,
            ValidTo = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 7),
            GrantedBySid = new Sid(reader.GetString(8)),
            ApproverCapacity = capacity,
            RequestId = ConsentWorkflowSql.ReadOptionalGuid(reader, 10),
            IsRevoked = ConsentWorkflowSql.ReadBooleanOrFalse(reader, 11),
            RevokedBySid = ConsentWorkflowSql.ReadOptionalSid(reader, 12),
            RevokedAt = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 13),
            RevokeReason = ConsentWorkflowSql.ReadOptionalString(reader, 14),
            CreatedAt = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 15) ?? DateTimeOffset.UnixEpoch
        };
    }

    private async Task<T> ExecuteWriteAsync<T>(
        DbTransaction? existingTx,
        Func<DbConnection, DbTransaction, Task<T>> work,
        CancellationToken ct)
    {
        if (existingTx is not null)
        {
            var conn = existingTx.Connection ?? throw new InvalidOperationException("Transaction is not associated with an open connection.");
            return await work(conn, existingTx).ConfigureAwait(false);
        }

        await using var ownedConn = await OpenAsync(ct).ConfigureAwait(false);
        await using var ownedTx = await ownedConn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await work(ownedConn, ownedTx).ConfigureAwait(false);
            await ownedTx.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await TransactionRollback.RollbackAsync(ownedTx).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<long> BumpEpochInTxAsync(DbConnection conn, DbTransaction tx, string tenantId, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = IsSqlServer ? AccessPolicySettingsStore.BumpEpochSsSql : AccessPolicySettingsStore.BumpEpochPgSql;
        cmd.AddBound("@tenantId", tenantId);
        cmd.AddBound("@updatedAt", now.ToUniversalTime(), DbType.DateTimeOffset);
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is null or DBNull ? 0L : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var conn = _dialect.CreateConnection();
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string RequireTenant(TenantId tenant)
    {
        var valid = TenantScope.RequireTenant(tenant, GovernanceTable.CatalogVisibilityGrants.ToSqlName());
        return StoredTenant.RequireWritable(valid);
    }
}
