namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

public partial class GovernanceRepository : IConsentEventOutbox, IGrantExpiryStore
{
    public async Task<long> AppendAsync(
        ConsentEventWrite entry,
        DbTransaction? transaction = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var tenantId = StoredTenant.Parse(entry.TenantId).Value;
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.ConsentEvents));
        StoredTenant.RequireWritable(tenantId);

        if (transaction is not null)
        {
            var txConn = transaction.Connection ?? throw new InvalidOperationException("Transaction has no active connection.");
            return await AppendConsentEventInTransactionAsync(txConn, transaction, entry, tenantId, ct).ConfigureAwait(false);
        }

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var seq = await AppendConsentEventInTransactionAsync(conn, tx, entry, tenantId, ct).ConfigureAwait(false);
            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            return seq;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<long> AppendConsentEventInTransactionAsync(
        DbConnection conn,
        DbTransaction tx,
        ConsentEventWrite entry,
        TenantId tenantId,
        CancellationToken ct)
    {
        var isPg = _dialect is PostgreSqlGovernanceDialect;
        await AcquireConsentEventSeqLockAsync(conn, tx, tenantId, isPg, ct).ConfigureAwait(false);

        long nextSeq;
        await using (var seqCmd = conn.CreateCommand())
        {
            seqCmd.Transaction = tx;
            seqCmd.CommandText = "SELECT COALESCE(MAX(seq), 0) + 1 FROM CONSENT_EVENTS WHERE tenant_id = @tenant";
            seqCmd.AddBound("@tenant", tenantId.Value);
            var rawSeq = await seqCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            nextSeq = Convert.ToInt64(rawSeq, CultureInfo.InvariantCulture);
        }

        await using (var insertCmd = conn.CreateCommand())
        {
            insertCmd.Transaction = tx;
            insertCmd.CommandText = @"INSERT INTO CONSENT_EVENTS (tenant_id, seq, id, type, subject_id, payload_json, created_at)
VALUES (@tenant, @seq, @id, @type, @subjectId, @payload, @createdAt)";
            insertCmd.AddBound("@tenant", tenantId.Value);
            insertCmd.AddBound("@seq", nextSeq, DbType.Int64);
            insertCmd.AddBound("@id", entry.EventId.ToString("D"));
            insertCmd.AddBound("@type", entry.EventType);
            insertCmd.AddBound("@subjectId", entry.SubjectId);
            insertCmd.AddBound("@payload", entry.PayloadJson);
            insertCmd.AddBound("@createdAt", entry.CreatedAt.ToUniversalTime(), DbType.DateTimeOffset);
            await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (entry.ExternalSystemId is { } externalSystemId)
        {
            await using var deliveryCmd = conn.CreateCommand();
            deliveryCmd.Transaction = tx;
            deliveryCmd.CommandText = isPg
                ? @"INSERT INTO CONSENT_EVENT_DELIVERIES (event_id, external_system_id, attempts, next_attempt_at)
SELECT @eventId, s.id, 0, @nextAttemptAt
FROM EXTERNAL_WORKFLOW_SYSTEMS s
WHERE s.id = @externalSystemId AND s.tenant_id = @tenant AND s.enabled = TRUE
ON CONFLICT (event_id, external_system_id) DO NOTHING"
                : @"INSERT INTO CONSENT_EVENT_DELIVERIES (event_id, external_system_id, attempts, next_attempt_at)
SELECT @eventId, s.id, 0, @nextAttemptAt
FROM EXTERNAL_WORKFLOW_SYSTEMS s
WHERE s.id = @externalSystemId AND s.tenant_id = @tenant AND s.enabled = 1
  AND NOT EXISTS (SELECT 1 FROM CONSENT_EVENT_DELIVERIES d WHERE d.event_id = @eventId AND d.external_system_id = s.id)";
            deliveryCmd.AddBound("@eventId", entry.EventId.ToString("D"));
            deliveryCmd.AddBound("@externalSystemId", externalSystemId.ToString("D"));
            deliveryCmd.AddBound("@tenant", tenantId.Value);
            deliveryCmd.AddBound("@nextAttemptAt", entry.CreatedAt.ToUniversalTime(), DbType.DateTimeOffset);
            await deliveryCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return nextSeq;
    }

    private static async Task AcquireConsentEventSeqLockAsync(
        DbConnection conn,
        DbTransaction tx,
        TenantId tenantId,
        bool isPg,
        CancellationToken ct)
    {
        await using var lockCmd = conn.CreateCommand();
        lockCmd.Transaction = tx;
        if (isPg)
        {
            lockCmd.CommandText = "SELECT pg_advisory_xact_lock(hashtext(@lockKey))";
            lockCmd.AddBound("@lockKey", "consent-events:" + tenantId.Value);
            await lockCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }
        else
        {
            lockCmd.CommandText = "EXEC sp_getapplock @Resource = @lockKey, @LockMode = 'Exclusive', @LockOwner = 'Transaction'";
            lockCmd.AddBound("@lockKey", "consent-events:" + tenantId.Value);
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<PendingConsentEventDelivery>> ClaimDeliveriesAsync(
        int limit,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        string leaseOwner,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        var effectiveLimit = Math.Clamp(limit, 1, 500);
        var nowUtc = now.ToUniversalTime();
        var leaseUntil = (now + leaseDuration).ToUniversalTime();
        var isPg = _dialect is PostgreSqlGovernanceDialect;

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        if (isPg)
        {
            cmd.CommandText = @"
WITH picked AS (
    SELECT d.event_id, d.external_system_id
    FROM CONSENT_EVENT_DELIVERIES d
    WHERE d.delivered_at IS NULL
      AND d.dead_lettered_at IS NULL
      AND d.next_attempt_at <= @now
      AND (d.lease_until IS NULL OR d.lease_until <= @now)
    ORDER BY d.next_attempt_at
    LIMIT @limit
    FOR UPDATE SKIP LOCKED
)
UPDATE CONSENT_EVENT_DELIVERIES d
SET lease_owner = @owner,
    lease_until = @leaseUntil,
    attempts = d.attempts + 1
FROM picked
JOIN CONSENT_EVENTS e ON e.id = picked.event_id
WHERE d.event_id = picked.event_id
  AND d.external_system_id = picked.external_system_id
RETURNING d.event_id, d.external_system_id, e.tenant_id, e.type, e.subject_id, e.payload_json, e.created_at, d.attempts, d.next_attempt_at;";
        }
        else
        {
            cmd.CommandText = @"
WITH picked AS (
    SELECT TOP (@limit) d.*
    FROM dbo.CONSENT_EVENT_DELIVERIES d WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE d.delivered_at IS NULL
      AND d.dead_lettered_at IS NULL
      AND d.next_attempt_at <= @now
      AND (d.lease_until IS NULL OR d.lease_until <= @now)
    ORDER BY d.next_attempt_at
)
UPDATE picked
SET lease_owner = @owner,
    lease_until = @leaseUntil,
    attempts = attempts + 1
OUTPUT inserted.event_id, inserted.external_system_id, e.tenant_id, e.type, e.subject_id, e.payload_json, e.created_at, inserted.attempts, inserted.next_attempt_at
FROM picked
JOIN dbo.CONSENT_EVENTS e ON e.id = picked.event_id;";
        }

        cmd.AddBound("@limit", effectiveLimit, DbType.Int32);
        cmd.AddBound("@now", nowUtc, DbType.DateTimeOffset);
        cmd.AddBound("@leaseUntil", leaseUntil, DbType.DateTimeOffset);
        cmd.AddBound("@owner", leaseOwner);

        var items = new List<PendingConsentEventDelivery>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(new PendingConsentEventDelivery(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                ConsentWorkflowSql.ReadOptionalTimestamp(reader, 6) ?? nowUtc,
                Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture),
                ConsentWorkflowSql.ReadOptionalTimestamp(reader, 8) ?? nowUtc));
        }

        return items;
    }

    public async Task MarkDeliveredAsync(
        Guid eventId,
        Guid externalSystemId,
        string leaseOwner,
        DateTimeOffset deliveredAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE CONSENT_EVENT_DELIVERIES
SET delivered_at = @deliveredAt, lease_until = NULL
WHERE event_id = @eventId AND external_system_id = @externalSystemId AND lease_owner = @owner";
        cmd.AddBound("@deliveredAt", deliveredAt.ToUniversalTime(), DbType.DateTimeOffset);
        cmd.AddBound("@eventId", eventId.ToString("D"));
        cmd.AddBound("@externalSystemId", externalSystemId.ToString("D"));
        cmd.AddBound("@owner", leaseOwner);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RescheduleDeliveryAsync(
        Guid eventId,
        Guid externalSystemId,
        string leaseOwner,
        DateTimeOffset nextAttemptAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE CONSENT_EVENT_DELIVERIES
SET next_attempt_at = @nextAttemptAt, lease_owner = NULL, lease_until = NULL
WHERE event_id = @eventId AND external_system_id = @externalSystemId AND lease_owner = @owner";
        cmd.AddBound("@nextAttemptAt", nextAttemptAt.ToUniversalTime(), DbType.DateTimeOffset);
        cmd.AddBound("@eventId", eventId.ToString("D"));
        cmd.AddBound("@externalSystemId", externalSystemId.ToString("D"));
        cmd.AddBound("@owner", leaseOwner);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkDeadLetteredAsync(
        Guid eventId,
        Guid externalSystemId,
        string leaseOwner,
        DateTimeOffset deadLetteredAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE CONSENT_EVENT_DELIVERIES
SET dead_lettered_at = @deadLetteredAt, lease_until = NULL
WHERE event_id = @eventId AND external_system_id = @externalSystemId AND lease_owner = @owner";
        cmd.AddBound("@deadLetteredAt", deadLetteredAt.ToUniversalTime(), DbType.DateTimeOffset);
        cmd.AddBound("@eventId", eventId.ToString("D"));
        cmd.AddBound("@externalSystemId", externalSystemId.ToString("D"));
        cmd.AddBound("@owner", leaseOwner);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<ConsentEventFeedPage> ReadFeedAsync(
        TenantId tenantId,
        long sinceSeq,
        int pageSize,
        Guid? externalSystemId = null,
        DateTimeOffset? retentionCutoff = null,
        CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.ConsentEvents));
        var limit = Math.Clamp(pageSize, 1, 200);
        var isPg = _dialect is PostgreSqlGovernanceDialect;

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);

        if (sinceSeq > 0 && retentionCutoff is { } cutoff)
        {
            await using var minCmd = conn.CreateCommand();
            minCmd.CommandText = "SELECT MIN(seq) FROM CONSENT_EVENTS WHERE tenant_id = @tenant";
            minCmd.AddBound("@tenant", tenantId.Value);
            var minRaw = await minCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (minRaw is not null && minRaw is not DBNull)
            {
                var minSeq = Convert.ToInt64(minRaw, CultureInfo.InvariantCulture);
                if (sinceSeq + 1 < minSeq)
                {
                    return new ConsentEventFeedPage([], sinceSeq, CursorExpired: true);
                }
            }
            else
            {
                // Empty log with an old cursor might reflect purged events; only treat as non-expired if minSeq missing.
                _ = cutoff;
            }
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = (isPg, externalSystemId.HasValue) switch
        {
            (true, false) => @"SELECT e.tenant_id, e.seq, e.id, e.type, e.subject_id, e.payload_json, e.created_at
FROM CONSENT_EVENTS e
WHERE e.tenant_id = @tenant AND e.seq > @since
ORDER BY e.seq ASC
LIMIT @limit",
            (true, true) => @"SELECT e.tenant_id, e.seq, e.id, e.type, e.subject_id, e.payload_json, e.created_at
FROM CONSENT_EVENTS e
JOIN CONSENT_EVENT_DELIVERIES d ON d.event_id = e.id AND d.external_system_id = @externalSystemId
WHERE e.tenant_id = @tenant AND e.seq > @since
ORDER BY e.seq ASC
LIMIT @limit",
            (false, false) => @"SELECT TOP (@limit) e.tenant_id, e.seq, e.id, e.type, e.subject_id, e.payload_json, e.created_at
FROM dbo.CONSENT_EVENTS e
WHERE e.tenant_id = @tenant AND e.seq > @since
ORDER BY e.seq ASC",
            (false, true) => @"SELECT TOP (@limit) e.tenant_id, e.seq, e.id, e.type, e.subject_id, e.payload_json, e.created_at
FROM dbo.CONSENT_EVENTS e
JOIN dbo.CONSENT_EVENT_DELIVERIES d ON d.event_id = e.id AND d.external_system_id = @externalSystemId
WHERE e.tenant_id = @tenant AND e.seq > @since
ORDER BY e.seq ASC"
        };

        cmd.AddBound("@tenant", tenantId.Value);
        cmd.AddBound("@since", Math.Max(0L, sinceSeq), DbType.Int64);
        cmd.AddBound("@limit", limit, DbType.Int32);
        if (externalSystemId.HasValue)
        {
            cmd.AddBound("@externalSystemId", externalSystemId.Value.ToString("D"));
        }

        var events = new List<ConsentEventRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            events.Add(new ConsentEventRecord(
                reader.GetString(0),
                Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                Guid.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                ConsentWorkflowSql.ReadOptionalTimestamp(reader, 6) ?? DateTimeOffset.UnixEpoch));
        }

        var nextSince = events.Count > 0 ? events[^1].Seq : Math.Max(0L, sinceSeq);
        return new ConsentEventFeedPage(events, nextSince, CursorExpired: false);
    }

    public async Task<int> PurgeExpiredEventsAsync(DateTimeOffset olderThan, CancellationToken ct = default)
    {
        var cutoff = olderThan.ToUniversalTime();
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var delDeliveries = conn.CreateCommand())
            {
                delDeliveries.Transaction = tx;
                delDeliveries.CommandText = @"DELETE FROM CONSENT_EVENT_DELIVERIES
WHERE event_id IN (SELECT id FROM CONSENT_EVENTS WHERE created_at < @cutoff)";
                delDeliveries.AddBound("@cutoff", cutoff, DbType.DateTimeOffset);
                await delDeliveries.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            int deleted;
            await using (var delEvents = conn.CreateCommand())
            {
                delEvents.Transaction = tx;
                delEvents.CommandText = "DELETE FROM CONSENT_EVENTS WHERE created_at < @cutoff";
                delEvents.AddBound("@cutoff", cutoff, DbType.DateTimeOffset);
                deleted = await delEvents.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            return deleted;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<ExpiringGrantCandidate>> GetGrantsWithoutExpiryNoticeAsync(
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var isPg = _dialect is PostgreSqlGovernanceDialect;
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = isPg
            ? @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
       c.grantee_sid, c.valid_from, c.valid_to,
       c.is_revoked, t.source_name, t.schema_name, t.table_name,
       c.tenant_id, c.renewal_count,
       c.grant_source, c.grant_record_seq, c.profile_assignment_id, c.expiry_notice_sent_at, c.grant_kind, c.approver_capacity, c.self_grant, c.target_columns,
       r.workflow_mode, r.external_system_id
FROM CONSENTS c
JOIN TABLES t ON c.table_id = t.id
LEFT JOIN CONSENT_REQUESTS r ON r.id = c.consent_request_id
WHERE c.is_revoked = 0 AND c.expiry_notice_sent_at IS NULL AND c.valid_to > @now
ORDER BY c.valid_to
LIMIT 500"
            : @"SELECT TOP (500) c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
       c.grantee_sid, c.valid_from, c.valid_to,
       c.is_revoked, t.source_name, t.schema_name, t.table_name,
       c.tenant_id, c.renewal_count,
       c.grant_source, c.grant_record_seq, c.profile_assignment_id, c.expiry_notice_sent_at, c.grant_kind, c.approver_capacity, c.self_grant, c.target_columns,
       r.workflow_mode, r.external_system_id
FROM dbo.CONSENTS c
JOIN dbo.TABLES t ON c.table_id = t.id
LEFT JOIN dbo.CONSENT_REQUESTS r ON r.id = c.consent_request_id
WHERE c.is_revoked = 0 AND c.expiry_notice_sent_at IS NULL AND c.valid_to > @now
ORDER BY c.valid_to";
        cmd.AddBound("@now", now.ToString("O"));

        return await ReadExpiringGrantCandidatesAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<bool> MarkExpiryNoticeSentAsync(
        ExpiringGrantCandidate candidate,
        DateTimeOffset sentAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var consent = candidate.Consent;
        TenantScope.RequireTenant(consent.TenantId, nameof(GovernanceTable.Consents));
        StoredTenant.RequireWritable(consent.TenantId);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            int updated;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"UPDATE CONSENTS
SET expiry_notice_sent_at = @sentAt
WHERE id = @id AND tenant_id = @tenant AND is_revoked = 0 AND expiry_notice_sent_at IS NULL";
                cmd.AddBound("@sentAt", sentAt.ToUniversalTime(), DbType.DateTimeOffset);
                cmd.AddBound("@id", consent.Id.ToString("D"));
                cmd.AddBound("@tenant", consent.TenantId.Value);
                updated = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (updated == 0)
            {
                await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
                return false;
            }

            await RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = consent.TenantId,
                EventType = AuditEventTypes.GrantExpiringNoticeSent,
                ActorSid = new Sid("SYSTEM"),
                TargetTable = consent.TableIdentifier.ToString(),
                Decision = "EXPIRING_NOTICE",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new { consentId = consent.Id, validUntil = consent.ValidTo })
            }, tx, ct).ConfigureAwait(false);

            var payload = ConsentEventPayloads.ForGrant(
                consent,
                candidate.WorkflowMode,
                1,
                "EXPIRING",
                sentAt);
            var eventId = Guid.NewGuid();
            await AppendConsentEventInTransactionAsync(
                conn,
                tx,
                new ConsentEventWrite(
                    consent.TenantId.Value,
                    eventId,
                    ConsentEventTypes.GrantExpiring,
                    consent.Id.ToString("D"),
                    ConsentEventPayloads.Serialize(payload),
                    sentAt,
                    candidate.ExternalSystemId),
                consent.TenantId,
                ct).ConfigureAwait(false);

            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<ExpiringGrantCandidate>> GetExpiredGrantsWithoutRecordAsync(
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var isPg = _dialect is PostgreSqlGovernanceDialect;
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = isPg
            ? @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
       c.grantee_sid, c.valid_from, c.valid_to,
       c.is_revoked, t.source_name, t.schema_name, t.table_name,
       c.tenant_id, c.renewal_count,
       c.grant_source, c.grant_record_seq, c.profile_assignment_id, c.expiry_notice_sent_at, c.grant_kind, c.approver_capacity, c.self_grant, c.target_columns,
       r.workflow_mode, r.external_system_id
FROM CONSENTS c
JOIN TABLES t ON c.table_id = t.id
LEFT JOIN CONSENT_REQUESTS r ON r.id = c.consent_request_id
WHERE c.is_revoked = 0 AND c.valid_to <= @now
  AND NOT EXISTS (
      SELECT 1 FROM GRANT_RECORDS gr
      WHERE gr.tenant_id = c.tenant_id AND gr.consent_id = c.id AND gr.record_type = 'EXPIRED'
  )
ORDER BY c.valid_to
LIMIT 500"
            : @"SELECT TOP (500) c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
       c.grantee_sid, c.valid_from, c.valid_to,
       c.is_revoked, t.source_name, t.schema_name, t.table_name,
       c.tenant_id, c.renewal_count,
       c.grant_source, c.grant_record_seq, c.profile_assignment_id, c.expiry_notice_sent_at, c.grant_kind, c.approver_capacity, c.self_grant, c.target_columns,
       r.workflow_mode, r.external_system_id
FROM dbo.CONSENTS c
JOIN dbo.TABLES t ON c.table_id = t.id
LEFT JOIN dbo.CONSENT_REQUESTS r ON r.id = c.consent_request_id
WHERE c.is_revoked = 0 AND c.valid_to <= @now
  AND NOT EXISTS (
      SELECT 1 FROM dbo.GRANT_RECORDS gr
      WHERE gr.tenant_id = c.tenant_id AND gr.consent_id = c.id AND gr.record_type = 'EXPIRED'
  )
ORDER BY c.valid_to";
        cmd.AddBound("@now", now.ToString("O"));

        return await ReadExpiringGrantCandidatesAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<bool> RecordGrantExpiryAsync(
        ExpiringGrantCandidate candidate,
        DateTimeOffset expiredAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var consent = candidate.Consent;
        TenantScope.RequireTenant(consent.TenantId, nameof(GovernanceTable.Consents));
        StoredTenant.RequireWritable(consent.TenantId);
        var grantWriter = RequireGrantWriter();

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var existsCmd = conn.CreateCommand())
            {
                existsCmd.Transaction = tx;
                existsCmd.CommandText = @"SELECT COUNT(*) FROM GRANT_RECORDS
WHERE tenant_id = @tenant AND consent_id = @consentId AND record_type = 'EXPIRED'";
                existsCmd.AddBound("@tenant", consent.TenantId.Value);
                existsCmd.AddBound("@consentId", consent.Id.ToString("D"));
                var count = Convert.ToInt64(await existsCmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
                if (count > 0)
                {
                    await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
                    return false;
                }
            }

            var granteeRef = consent.GranteeSid?.Value ?? string.Empty;
            var requestedBy = consent.CreatedBySid is { } creatorSid && !string.IsNullOrWhiteSpace(creatorSid.Value)
                ? creatorSid.Value
                : "SYSTEM";
            var input = new GrantRecordInput(
                consent.TenantId.Value,
                JsonSerializer.Serialize(new { table = consent.TableIdentifier.ToString(), granteeType = consent.GranteeType.ToString() }),
                requestedBy,
                JsonSerializer.Serialize(new { consentId = consent.Id, reason = "EXPIRED" }),
                expiredAt,
                consent.TableIdentifier.ToString(),
                consent.GranteeType.ToString(),
                granteeRef,
                consent.ConsentRequestId,
                consent.GrantSource,
                consent.ProfileAssignmentId,
                consent.ValidFrom,
                consent.ValidTo,
                GrantRecordType.Expired);

            await grantWriter.RecordExpiryAsync(tx, consent, input, ct).ConfigureAwait(false);

            await RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = consent.TenantId,
                EventType = AuditEventTypes.GrantExpired,
                ActorSid = new Sid("SYSTEM"),
                TargetTable = consent.TableIdentifier.ToString(),
                Decision = "EXPIRED",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new { consentId = consent.Id, validUntil = consent.ValidTo })
            }, tx, ct).ConfigureAwait(false);

            var payload = ConsentEventPayloads.ForGrant(
                consent,
                candidate.WorkflowMode,
                1,
                "EXPIRED",
                expiredAt);
            await AppendConsentEventInTransactionAsync(
                conn,
                tx,
                new ConsentEventWrite(
                    consent.TenantId.Value,
                    Guid.NewGuid(),
                    ConsentEventTypes.GrantExpired,
                    consent.Id.ToString("D"),
                    ConsentEventPayloads.Serialize(payload),
                    expiredAt,
                    candidate.ExternalSystemId),
                consent.TenantId,
                ct).ConfigureAwait(false);

            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<ConsentRequest>> GetOpenPendingRequestsAsync(
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var isPg = _dialect is PostgreSqlGovernanceDialect;
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = isPg
            ? @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
       r.business_justification, r.status, r.requested_at, r.requested_valid_to,
       t.source_name, t.schema_name, t.table_name, r.tenant_id,
       r.requester_identifiers_json, r.context_json, r.requested_write_rules_json,
       r.version, r.workflow_mode, r.external_system_id, r.external_reference, r.requested_valid_from, r.updated_at, r.request_kind, r.profile_id, r.grant_kind, r.target_columns, r.container_kind, r.container_id, r.approver_capacity, r.self_grant, r.bundle_id, r.decided_by, r.decided_at
FROM CONSENT_REQUESTS r
LEFT JOIN TABLES t ON r.table_id = t.id
WHERE r.status IN ('PENDING', 'PENDING_SECOND_APPROVAL', 'PENDING_EXTERNAL_APPROVAL')
ORDER BY r.requested_at ASC
LIMIT 500"
            : @"SELECT TOP (500) r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
       r.business_justification, r.status, r.requested_at, r.requested_valid_to,
       t.source_name, t.schema_name, t.table_name, r.tenant_id,
       r.requester_identifiers_json, r.context_json, r.requested_write_rules_json,
       r.version, r.workflow_mode, r.external_system_id, r.external_reference, r.requested_valid_from, r.updated_at, r.request_kind, r.profile_id, r.grant_kind, r.target_columns, r.container_kind, r.container_id, r.approver_capacity, r.self_grant, r.bundle_id, r.decided_by, r.decided_at
FROM dbo.CONSENT_REQUESTS r
LEFT JOIN dbo.TABLES t ON r.table_id = t.id
WHERE r.status IN ('PENDING', 'PENDING_SECOND_APPROVAL', 'PENDING_EXTERNAL_APPROVAL')
ORDER BY r.requested_at ASC";

        var list = new List<ConsentRequest>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadConsentRequestRow(reader));
        }

        return list;
    }

    public async Task<bool> ExpirePendingRequestAsync(
        ConsentRequest request,
        DateTimeOffset expiredAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        TenantScope.RequireTenant(request.TenantId, nameof(GovernanceTable.ConsentRequests));
        StoredTenant.RequireWritable(request.TenantId);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            int updated;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"UPDATE CONSENT_REQUESTS
SET status = 'EXPIRED', version = version + 1, updated_at = @now
WHERE id = @id AND tenant_id = @tenant
  AND status IN ('PENDING', 'PENDING_SECOND_APPROVAL', 'PENDING_EXTERNAL_APPROVAL')";
                cmd.AddBound("@now", expiredAt.ToUniversalTime(), DbType.DateTimeOffset);
                cmd.AddBound("@id", request.Id.ToString("D"));
                cmd.AddBound("@tenant", request.TenantId.Value);
                updated = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (updated == 0)
            {
                await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
                return false;
            }

            var nextVersion = (request.Version <= 0 ? 1 : request.Version) + 1;

            await RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = request.TenantId,
                EventType = AuditEventTypes.ConsentRequestExpired,
                ActorSid = new Sid("SYSTEM"),
                TargetTable = request.TableIdentifier.ToString(),
                Decision = "EXPIRED",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new { requestId = request.Id })
            }, tx, ct).ConfigureAwait(false);

            var payload = new ConsentThinEventPayload(
                request.TenantId.Value,
                request.Id,
                null,
                request.TableIdentifier.ToString(),
                "EXPIRED",
                nextVersion,
                request.WorkflowMode.ToSqlString(),
                expiredAt,
                new ConsentEventLinks($"/api/v1/consent/requests/{request.Id}", "/api/v1/consent/events"));
            await AppendConsentEventInTransactionAsync(
                conn,
                tx,
                new ConsentEventWrite(
                    request.TenantId.Value,
                    Guid.NewGuid(),
                    ConsentEventTypes.RequestExpired,
                    request.Id.ToString("D"),
                    ConsentEventPayloads.Serialize(payload),
                    expiredAt,
                    request.ExternalSystemId),
                request.TenantId,
                ct).ConfigureAwait(false);

            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task<IReadOnlyList<ExpiringGrantCandidate>> ReadExpiringGrantCandidatesAsync(
        DbCommand cmd,
        CancellationToken ct)
    {
        var results = new List<ExpiringGrantCandidate>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var granteeSidStr = reader.IsDBNull(5) ? null : reader.GetString(5);
            var consent = ConsentWorkflowSql.BuildConsent(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                new TableIdentifier(reader.GetString(9), reader.GetString(10), reader.GetString(11)),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                Enum.Parse<ConsentEffect>(reader.GetString(3), ignoreCase: true),
                GranteeTypeParsing.ParseStored(reader.GetString(4)),
                !string.IsNullOrEmpty(granteeSidStr) ? new Sid(granteeSidStr) : (Sid?)null,
                ConsentWorkflowSql.ReadOptionalTimestamp(reader, 6) ?? DateTimeOffset.UnixEpoch,
                ConsentWorkflowSql.ReadOptionalTimestamp(reader, 7) ?? DateTimeOffset.MaxValue,
                Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture) != 0,
                // Owner grants of a catalog-wide assignment live in the platform bucket; the sweep reads every tenant.
                StoredTenant.ParseAudit(reader.GetString(12)),
                reader,
                14,
                Convert.ToInt32(reader.GetValue(13), CultureInfo.InvariantCulture));
            var workflowMode = ConsentWorkflowSql.ReadWorkflowMode(reader, 22);
            var externalSystemId = ConsentWorkflowSql.ReadOptionalGuid(reader, 23);
            results.Add(new ExpiringGrantCandidate(consent, workflowMode, externalSystemId));
        }

        return results;
    }

    private static ConsentRequest ReadConsentRequestRow(DbDataReader reader)
    {
        var ids = reader.IsDBNull(13)
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(reader.GetString(13)) ?? new List<string>();
        var writeRules = reader.IsDBNull(15)
            ? Array.Empty<WriteRule>()
            : (IReadOnlyList<WriteRule>)(JsonSerializer.Deserialize<List<WriteRule>>(reader.GetString(15)) ?? new List<WriteRule>());
        return ConsentWorkflowSql.BuildConsentRequest(
            reader,
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            new Sid(reader.GetString(2)),
            GranteeTypeParsing.ParseStored(reader.GetString(3)),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            ConsentWorkflowSql.ReadOptionalTimestamp(reader, 7) ?? DateTimeOffset.UnixEpoch,
            ConsentWorkflowSql.ReadOptionalTimestamp(reader, 8) ?? DateTimeOffset.UnixEpoch,
            reader.IsDBNull(9) ? default : new TableIdentifier(reader.GetString(9), reader.GetString(10), reader.GetString(11)),
            StoredTenant.Parse(reader.GetString(12)).Value,
            ids,
            reader.IsDBNull(14) ? null : reader.GetString(14),
            writeRules,
            true,
            16);
    }
}
