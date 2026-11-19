namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests.ProfileAssignments;
using Autheris.Application.Governance.Grants;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

public partial class GovernanceRepository : IOwnerGrantStore, IProfileAssignmentStore
{
    private sealed class ProfileAssignmentDbTx(GovernanceRepository repo, DbConnection conn, DbTransaction tx) : IProfileAssignmentTransaction
    {
        private bool _completed;
        public DbTransaction Transaction => tx;

        public async Task CommitAsync(CancellationToken ct = default)
        {
            await repo.CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            _completed = true;
        }

        public async Task RollbackAsync(CancellationToken ct = default)
        {
            await repo.RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                try { await tx.RollbackAsync().ConfigureAwait(false); } catch { }
            }

            await tx.DisposeAsync().ConfigureAwait(false);
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    private const string PgUpsertProfileItemSql = @"
INSERT INTO PROFILE_ASSIGNMENT_ITEMS
    (tenant_id, request_id, item_no, object_ref, owner_sid, owner_decision, decided_by, decided_at)
VALUES
    (@tenantId, @requestId, @itemNo, @objectRef, @ownerSid, @decision, @decidedBy, @decidedAt)
ON CONFLICT (tenant_id, request_id, item_no, owner_sid) DO UPDATE SET
    object_ref = EXCLUDED.object_ref,
    owner_decision = EXCLUDED.owner_decision,
    decided_by = EXCLUDED.decided_by,
    decided_at = EXCLUDED.decided_at";

    private const string SsUpsertProfileItemSql = @"
MERGE dbo.PROFILE_ASSIGNMENT_ITEMS AS target
USING (SELECT @tenantId AS tenant_id, @requestId AS request_id, @itemNo AS item_no, @ownerSid AS owner_sid) AS src
   ON target.tenant_id = src.tenant_id AND target.request_id = src.request_id
  AND target.item_no = src.item_no AND target.owner_sid = src.owner_sid
WHEN MATCHED THEN UPDATE SET
    object_ref = @objectRef,
    owner_decision = @decision,
    decided_by = @decidedBy,
    decided_at = @decidedAt
WHEN NOT MATCHED THEN
    INSERT (tenant_id, request_id, item_no, object_ref, owner_sid, owner_decision, decided_by, decided_at)
    VALUES (@tenantId, @requestId, @itemNo, @objectRef, @ownerSid, @decision, @decidedBy, @decidedAt);";

    private const string SelectProfileItemsSql = @"
SELECT tenant_id, request_id, item_no, object_ref, owner_sid, owner_decision, decided_by, decided_at
FROM PROFILE_ASSIGNMENT_ITEMS
WHERE request_id = @requestId
ORDER BY item_no ASC, owner_sid ASC";

    private const string DecideProfileItemsAllSql = @"
UPDATE PROFILE_ASSIGNMENT_ITEMS
SET owner_decision = @decision, decided_by = @decidedBy, decided_at = @decidedAt
WHERE request_id = @requestId AND owner_decision = 'PENDING'";

    private const string DecideProfileItemsByOwnerSql = @"
UPDATE PROFILE_ASSIGNMENT_ITEMS
SET owner_decision = @decision, decided_by = @decidedBy, decided_at = @decidedAt
WHERE request_id = @requestId AND owner_decision = 'PENDING' AND UPPER(owner_sid) = UPPER(@ownerSid)";

    private const string SelectActiveOwnerGrantsSql = @"
SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
       c.grantee_sid, c.valid_from, c.valid_to,
       c.is_revoked, c.tenant_id, c.renewal_count,
       c.grant_source, c.grant_record_seq, c.profile_assignment_id, c.expiry_notice_sent_at,
       t.source_name, t.schema_name, t.table_name
FROM CONSENTS c
JOIN TABLES t ON c.table_id = t.id
WHERE c.table_id = @tid
  AND c.is_revoked = 0
  AND UPPER(c.grantee_sid) = UPPER(@sid)
  AND c.grant_source = @grantSource";

    private const string CountActiveTableOwnershipSql = @"
SELECT COUNT(*)
FROM TABLE_OWNERS
WHERE table_id = @tid AND data_owner_id = @oid";

    private const string SelectActiveProfileConsentsSql = @"
SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
       c.grantee_sid, c.valid_from, c.valid_to,
       c.is_revoked, c.tenant_id, c.renewal_count,
       c.grant_source, c.grant_record_seq, c.profile_assignment_id, c.expiry_notice_sent_at,
       t.source_name, t.schema_name, t.table_name,
       c.object_pattern, c.data_source, c.column_set_mode, c.workflow_mode, c.profile_item_no
FROM CONSENTS c
LEFT JOIN TABLES t ON c.table_id = t.id
WHERE c.tenant_id = @tenantId
  AND c.profile_assignment_id = @assignmentId
  AND c.is_revoked = 0";

    async Task<IReadOnlyList<Consent>> IOwnerGrantStore.GetActiveOwnerGrantsAsync(
        DbTransaction transaction, Guid tableId, Sid ownerSid, CancellationToken ct)
    {
        var conn = transaction.Connection ?? throw new InvalidOperationException("Transaction connection unavailable.");
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = SelectActiveOwnerGrantsSql;
        cmd.AddBound("@tid", tableId.ToString("D"));
        cmd.AddBound("@sid", ownerSid.Value);
        cmd.AddBound("@grantSource", ConsentGrantSource.Owner.ToSqlString());

        var rows = new List<Consent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(ReadConsentRowWithTable(reader, hasPatternCols: false));
        }

        return rows;
    }

    async Task<bool> IOwnerGrantStore.HasActiveTableOwnershipAsync(
        DbTransaction transaction, Guid tableId, string ownerId, CancellationToken ct)
    {
        var conn = transaction.Connection ?? throw new InvalidOperationException("Transaction connection unavailable.");
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = CountActiveTableOwnershipSql;
        cmd.AddBound("@tid", tableId.ToString("D"));
        cmd.AddBound("@oid", ownerId);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return count > 0;
    }

    async Task IOwnerGrantStore.MarkOwnerGrantRevokedAsync(
        DbTransaction transaction, Guid consentId, Sid actorSid, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var conn = transaction.Connection ?? throw new InvalidOperationException("Transaction connection unavailable.");
        await ConsentGrantSqlCore.MarkRevokedAsync(conn, transaction, consentId, actorSid, reason, now, ct).ConfigureAwait(false);
    }

    Task IOwnerGrantStore.IncrementTableEpochAsync(DbTransaction transaction, TableIdentifier table, CancellationToken ct) =>
        IncrementTableEpochInternalAsync(transaction.Connection!, transaction, table, ct);

    Task IOwnerGrantStore.RecordAuditInTransactionAsync(DbTransaction transaction, AuditLogEntry entry, CancellationToken ct) =>
        RecordAuditEventAsync(entry, transaction, ct);

    async Task<IProfileAssignmentTransaction> IProfileAssignmentStore.BeginTransactionAsync(CancellationToken ct)
    {
        var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        return new ProfileAssignmentDbTx(this, conn, tx);
    }

    async Task IProfileAssignmentStore.UpsertAssignmentItemsAsync(
        DbTransaction? transaction, IReadOnlyList<ProfileAssignmentItemRow> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }

        var ownedConn = transaction is null ? await OpenConnectionAsync(ct).ConfigureAwait(false) : null;
        var conn = transaction?.Connection ?? ownedConn!;
        var sql = _dialect is PostgreSqlGovernanceDialect ? PgUpsertProfileItemSql : SsUpsertProfileItemSql;
        try
        {
            foreach (var item in items)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                cmd.AddBound("@tenantId", StoredTenant.RequireWritable(item.TenantId));
                cmd.AddBound("@requestId", item.RequestId.ToString("D"));
                cmd.AddBound("@itemNo", item.ItemNo, DbType.Int32);
                cmd.AddBound("@objectRef", item.ObjectRef);
                cmd.AddBound("@ownerSid", item.OwnerSid);
                cmd.AddBound("@decision", item.OwnerDecision);
                cmd.AddBound("@decidedBy", (object?)item.DecidedBy ?? DBNull.Value);
                cmd.AddBound("@decidedAt", item.DecidedAt.HasValue ? item.DecidedAt.Value.ToUniversalTime() : DBNull.Value, DbType.DateTimeOffset);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (ownedConn is not null)
            {
                await ownedConn.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    async Task<IReadOnlyList<ProfileAssignmentItemRow>> IProfileAssignmentStore.GetAssignmentItemsAsync(
        DbTransaction? transaction, Guid requestId, CancellationToken ct)
    {
        var ownedConn = transaction is null ? await OpenConnectionAsync(ct).ConfigureAwait(false) : null;
        var conn = transaction?.Connection ?? ownedConn!;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = SelectProfileItemsSql;
            cmd.AddBound("@requestId", requestId.ToString("D"));

            var list = new List<ProfileAssignmentItemRow>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(new ProfileAssignmentItemRow(
                    StoredTenant.Parse(reader.GetString(0)).Value,
                    Guid.Parse(reader.GetString(1)),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    ConsentWorkflowSql.ReadOptionalTimestamp(reader, 7)));
            }

            return list;
        }
        finally
        {
            if (ownedConn is not null)
            {
                await ownedConn.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    async Task<int> IProfileAssignmentStore.DecideAssignmentItemsAsync(
        DbTransaction transaction, Guid requestId, IReadOnlyCollection<string> targetOwnerSids,
        string decision, string decidedBy, DateTimeOffset decidedAt, bool decideAllPending, CancellationToken ct)
    {
        var conn = transaction.Connection ?? throw new InvalidOperationException("Transaction connection unavailable.");
        if (decideAllPending)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = DecideProfileItemsAllSql;
            cmd.AddBound("@decision", decision);
            cmd.AddBound("@decidedBy", decidedBy);
            cmd.AddBound("@decidedAt", decidedAt.ToUniversalTime(), DbType.DateTimeOffset);
            cmd.AddBound("@requestId", requestId.ToString("D"));
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        int updated = 0;
        foreach (var ownerSid in targetOwnerSids)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = DecideProfileItemsByOwnerSql;
            cmd.AddBound("@decision", decision);
            cmd.AddBound("@decidedBy", decidedBy);
            cmd.AddBound("@decidedAt", decidedAt.ToUniversalTime(), DbType.DateTimeOffset);
            cmd.AddBound("@requestId", requestId.ToString("D"));
            cmd.AddBound("@ownerSid", ownerSid);
            updated += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return updated;
    }

    async Task<IReadOnlyList<Consent>> IProfileAssignmentStore.GetConsentsByProfileAssignmentAsync(
        DbTransaction? transaction, TenantId tenantId, Guid profileAssignmentId, CancellationToken ct)
    {
        var ownedConn = transaction is null ? await OpenConnectionAsync(ct).ConfigureAwait(false) : null;
        var conn = transaction?.Connection ?? ownedConn!;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = SelectActiveProfileConsentsSql;
            cmd.AddBound("@tenantId", StoredTenant.RequireWritable(tenantId));
            cmd.AddBound("@assignmentId", profileAssignmentId.ToString("D"));

            var list = new List<Consent>();
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(ReadConsentRowWithTable(reader, hasPatternCols: true));
            }

            if (list.Count > 0)
            {
                if (transaction is null)
                {
                    await HydrateConsentsBatchAsync(conn, list, ct).ConfigureAwait(false);
                }
                else
                {
                    await using var readConn = await OpenConnectionAsync(ct).ConfigureAwait(false);
                    await HydrateConsentsBatchAsync(readConn, list, ct).ConfigureAwait(false);
                }
            }

            return list;
        }
        finally
        {
            if (ownedConn is not null)
            {
                await ownedConn.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    Task<Consent?> IProfileAssignmentStore.GetConsentByIdInTransactionAsync(
        DbTransaction transaction, Guid consentId, CancellationToken ct) =>
        GetConsentByIdAsync(consentId, ct);

    async Task IProfileAssignmentStore.MarkConsentRevokedInTransactionAsync(
        DbTransaction transaction, Guid consentId, Sid actorSid, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var conn = transaction.Connection ?? throw new InvalidOperationException("Transaction connection unavailable.");
        await ConsentGrantSqlCore.MarkRevokedAsync(conn, transaction, consentId, actorSid, reason, now, ct).ConfigureAwait(false);
    }

    async Task IProfileAssignmentStore.AddApprovingOwnersInTransactionAsync(
        DbTransaction transaction, Consent consent, IReadOnlyCollection<string> ownerSids, CancellationToken ct)
    {
        consent.ApprovingOwners = [.. ownerSids];
        await ConsentPatternSqlCore.InsertApprovingOwnersAsync(transaction, consent, ct).ConfigureAwait(false);
    }

    Task IProfileAssignmentStore.IncrementTableEpochInTransactionAsync(
        DbTransaction transaction, TableIdentifier table, CancellationToken ct) =>
        IncrementTableEpochInternalAsync(transaction.Connection!, transaction, table, ct);

    Task IProfileAssignmentStore.RecordAuditInTransactionAsync(
        DbTransaction transaction, AuditLogEntry entry, CancellationToken ct) =>
        RecordAuditEventAsync(entry, transaction, ct);

    async Task IProfileAssignmentStore.ApplyRequestStepAndStatusInTransactionAsync(
        DbTransaction transaction, ConsentRequest req, string? newStatus, Sid approverSid,
        string stepDecision, string? reason, DateTimeOffset decidedAt, CancellationToken ct)
    {
        var conn = transaction.Connection ?? throw new InvalidOperationException("Transaction connection unavailable.");
        if (stepDecision == "APPROVED")
        {
            await using var maxCmd = conn.CreateCommand();
            maxCmd.Transaction = transaction;
            maxCmd.CommandText = "SELECT COALESCE(MAX(step_number), 0) + 1 FROM APPROVAL_STEPS WHERE consent_request_id = @id";
            maxCmd.AddBound("@id", req.Id.ToString());
            int stepNumber = Convert.ToInt32(await maxCmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            await ConsentSqlCore.InsertApprovedStepAsync(conn, transaction, Guid.NewGuid(), req.Id, stepNumber, approverSid, decidedAt, ct).ConfigureAwait(false);
        }
        else
        {
            await ConsentSqlCore.InsertRejectedStepAsync(conn, transaction, req.Id, approverSid, reason ?? stepDecision, decidedAt, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(newStatus))
        {
            await ConsentSqlCore.ApplyGuardedStatusAsync(conn, transaction, null, req.Id, newStatus, req.Status, ct, decidedAt).ConfigureAwait(false);
        }
    }

    async Task IProfileAssignmentStore.InvalidateTablesAfterCommitAsync(
        IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct)
    {
        foreach (var table in tables)
        {
            if (!string.IsNullOrWhiteSpace(table.TableName))
            {
                await InvalidateTableEpochAsync(table, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task SyncOwnerGrantsInTransactionAsync(
        DbConnection conn, DbTransaction tx, DataOwnerAssignmentRequest request,
        IReadOnlyList<DataOwnerChange> changes, CancellationToken ct)
    {
        if (_grantWriter is null || changes.Count == 0)
        {
            return;
        }

        var ownerService = new OwnerGrantService(_grantWriter, this);
        var tableSql = _dialect is PostgreSqlGovernanceDialect ? TableLookupPgSql : TableLookupSsSql;
        var actor = request.CallerIdentifiers is { Count: > 0 }
            ? new Sid(request.CallerIdentifiers.First())
            : new Sid("system:ownership");

        foreach (var change in changes)
        {
            if (change.Kind != DataOwnerChangeKind.OwnerAssigned && change.Kind != DataOwnerChangeKind.OwnerRemoved)
            {
                continue;
            }

            var tableIdRaw = await ReadTableIdAsync(conn, tx, tableSql, change.Table, ct).ConfigureAwait(false);
            if (tableIdRaw is null || !Guid.TryParse(tableIdRaw, out var tableGuid) || string.IsNullOrWhiteSpace(change.OwnerSid))
            {
                continue;
            }

            var tenant = ResolveGrantTenant(request.TenantId);
            if (change.Kind == DataOwnerChangeKind.OwnerAssigned)
            {
                await ownerService.GrantOwnerAsync(tx, new OwnerGrantAssignment(
                    tableGuid, change.Table, change.OwnerId, new Sid(change.OwnerSid),
                    change.Role ?? "Owner", tenant, actor, request.CallerIdentifiers), ct).ConfigureAwait(false);
            }
            else
            {
                await ownerService.RevokeOwnerAsync(tx, new OwnerGrantRemoval(
                    tableGuid, change.Table, change.OwnerId, new Sid(change.OwnerSid),
                    change.Role ?? "Owner", tenant, actor, "Data owner assignment removed."), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The catalog is global (TABLES has no tenant column), so an owner grant takes the tenant of the request, else the platform tenant.</summary>
    private static TenantId ResolveGrantTenant(TenantId? requestTenant) => requestTenant ?? TenantId.Platform;

    private static Consent ReadConsentRowWithTable(DbDataReader reader, bool hasPatternCols)
    {
        var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);
        var tableId = reader.IsDBNull(1) ? Guid.Empty : Guid.Parse(reader.GetString(1));
        var table = reader.IsDBNull(15)
            ? default
            : new TableIdentifier(reader.GetString(15), reader.GetString(16), reader.GetString(17));

        return new Consent
        {
            Id = Guid.Parse(reader.GetString(0)),
            TableId = tableId,
            TableIdentifier = table,
            ConsentRequestId = ConsentWorkflowSql.ReadOptionalGuid(reader, 2),
            Effect = Enum.Parse<ConsentEffect>(reader.GetString(3), ignoreCase: true),
            GranteeType = Enum.Parse<GranteeType>(reader.GetString(4), ignoreCase: true),
            GranteeSid = !string.IsNullOrEmpty(granteeSidStr) ? new Sid(granteeSidStr) : (Sid?)null,
            ValidFrom = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 6) ?? DateTimeOffset.MinValue,
            ValidTo = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 7) ?? DateTimeOffset.MaxValue,
            IsRevoked = Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture) == 1,
            TenantId = StoredTenant.ParseAudit(ConsentWorkflowSql.ReadOptionalString(reader, 9)),
            RenewalCount = Convert.ToInt32(reader.GetValue(10), CultureInfo.InvariantCulture),
            GrantSource = ConsentWorkflowSql.ReadGrantSource(reader, 11),
            GrantRecordSeq = ConsentWorkflowSql.ReadInt64OrZero(reader, 12),
            ProfileAssignmentId = ConsentWorkflowSql.ReadOptionalGuid(reader, 13),
            ExpiryNoticeSentAt = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 14),
            ObjectPattern = hasPatternCols ? ConsentWorkflowSql.ReadOptionalString(reader, 18) : null,
            DataSource = hasPatternCols ? ConsentWorkflowSql.ReadOptionalString(reader, 19) : null,
            ColumnSetMode = hasPatternCols
                ? ColumnSetModeExtensions.ParseColumnSetModeOrDefault(ConsentWorkflowSql.ReadOptionalString(reader, 20))
                : ColumnSetMode.Explicit,
            WorkflowMode = hasPatternCols
                ? ConsentWorkflowSqlMapping.ParseWorkflowModeOrDefault(ConsentWorkflowSql.ReadOptionalString(reader, 21))
                : ConsentWorkflowMode.Internal,
            ProfileItemNo = hasPatternCols && reader.FieldCount > 22 && !reader.IsDBNull(22) ? reader.GetInt32(22) : null
        };
    }
}
