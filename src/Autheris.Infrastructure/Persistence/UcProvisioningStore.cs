using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.Governance.Provisioning;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL and SQL Server persistence of Unity Catalog provisioning (issue #1122): the desired-state read over the consents of
/// one tenant on one data source, the ledger of applied privileges (<c>uc_provisioned_privileges</c>) and the per-consent feedback
/// (<c>uc_grant_provisioning</c>). Writes run in one governance transaction with their audit entries
/// (<see cref="GovernanceRepository.WriteWithAuditAsync{T}"/>); a failing audit insert rolls the rows back. Every write first checks the
/// writer's cluster fencing token against the data source's fence row (<c>uc_provisioning_fences</c>), so a reconciler that lost its
/// lease cannot change the ledger any more. Data source names and external ids are stored in lower case on both engines.
/// </summary>
public sealed class UcProvisioningStore(
    GovernanceRepository repository,
    IGovernanceSqlDialect dialect,
    int consentPageSize = UcProvisioningStore.ConsentPageSize,
    TimeProvider? timeProvider = null)
    : IUcProvisioningStore
{
    /// <summary>Consents read per page of the desired-state read.</summary>
    public const int ConsentPageSize = 500;

    private const string PgFence = "uc_provisioning_fences";
    private const string SsFence = "dbo.UC_PROVISIONING_FENCES";
    private const string FenceUpdate = " SET fencing_token = @fencingToken, updated_at = @fencedAt WHERE data_source = @fenceSource AND fencing_token <= @fencingToken";
    private const string FenceInsert = " (data_source, fencing_token, updated_at) VALUES (@fenceSource, @fencingToken, @fencedAt)";
    private const string FenceExists = " WHERE data_source = @fenceSource";

    private const string PgLedger = "uc_provisioned_privileges";
    private const string SsLedger = "dbo.UC_PROVISIONED_PRIVILEGES";
    private const string PgFeedback = "uc_grant_provisioning";
    private const string SsFeedback = "dbo.UC_GRANT_PROVISIONING";

    private const string LedgerColumns =
        "id, data_source, securable_type, full_name, principal, principal_external_id, privilege, state, attempts, next_attempt_at, " +
        "last_error_code, applied_at, verified_at, created_at, updated_at";

    private const string LedgerKeyWhere =
        " WHERE data_source = @dataSource AND securable_type = @securableType AND full_name = @fullName" +
        " AND principal_external_id = @principalExternalId AND privilege = @privilege";

    private const string LedgerUpdateSet =
        " SET principal = @principal, state = @state, attempts = @attempts, next_attempt_at = @nextAttemptAt, last_error_code = @lastErrorCode," +
        " applied_at = @appliedAt, verified_at = @verifiedAt, updated_at = @updatedAt";

    private const string LedgerInsertValues =
        " (" + LedgerColumns + ") VALUES (@id, @dataSource, @securableType, @fullName, @principal, @principalExternalId, @privilege, @state," +
        " @attempts, @nextAttemptAt, @lastErrorCode, @appliedAt, @verifiedAt, @createdAt, @updatedAt)";

    private const string FeedbackColumns = "tenant_id, consent_id, data_source, state, reason_code, updated_at, verified_at";

    private const string FeedbackUpdateSet =
        " SET data_source = @dataSource, state = @state, reason_code = @reasonCode, updated_at = @updatedAt, verified_at = @verifiedAt" +
        " WHERE tenant_id = @tenantId AND consent_id = @consentId";

    private const string FeedbackInsertValues =
        " (" + FeedbackColumns + ") VALUES (@tenantId, @consentId, @dataSource, @state, @reasonCode, @updatedAt, @verifiedAt)";

    private const string ConsentSelectBody =
        " c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type, c.grantee_sid, c.valid_from, c.valid_to, c.is_revoked," +
        " t.source_name, t.schema_name, t.table_name, c.tenant_id, c.renewal_count, " + ConsentWorkflowSql.ConsentColumns +
        " FROM CONSENTS c JOIN TABLES t ON c.table_id = t.id" +
        " WHERE c.is_revoked = 0 AND c.object_pattern IS NULL AND c.tenant_id = @tenantId AND LOWER(t.source_name) = @dataSource" +
        " AND c.valid_to > @endedBefore AND c.id > @after";

    private readonly GovernanceRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly IGovernanceSqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));

    private bool IsSqlServer => _dialect is SqlServerGovernanceDialect;

    private string Ledger => IsSqlServer ? SsLedger : PgLedger;

    private string Feedback => IsSqlServer ? SsFeedback : PgFeedback;

    private string Fence => IsSqlServer ? SsFence : PgFence;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    private readonly int _pageSize = consentPageSize > 0 ? consentPageSize : throw new ArgumentOutOfRangeException(nameof(consentPageSize));

    public async Task<IReadOnlyList<Consent>> ListTableConsentsAsync(TenantId tenant, string dataSource, DateTimeOffset now, CancellationToken ct)
    {
        var tenantValue = RequireTenant(tenant);
        var source = RequireDataSource(dataSource);

        // Consents that ended more than a day ago are skipped in SQL (text timestamps compare safely with that margin); the caller
        // applies the exact window. All pages come from one transaction, a snapshot where the engine offers one.
        var endedBefore = now.ToUniversalTime().AddDays(-1).ToString("O", CultureInfo.InvariantCulture);
        var result = new List<Consent>();
        await using var conn = await _repository.OpenConnectionAsync(ct).ConfigureAwait(false);
        var isolation = await ReadIsolationAsync(conn, ct).ConfigureAwait(false);
        await using (var tx = await conn.BeginTransactionAsync(isolation, ct).ConfigureAwait(false))
        {
            var after = string.Empty;
            while (true)
            {
                var page = 0;
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = IsSqlServer
                        ? "SELECT TOP (@max)" + ConsentSelectBody + " ORDER BY c.id"
                        : "SELECT" + ConsentSelectBody + " ORDER BY c.id LIMIT @max";
                    cmd.AddBound("@tenantId", tenantValue);
                    cmd.AddBound("@dataSource", source);
                    cmd.AddBound("@endedBefore", endedBefore);
                    cmd.AddBound("@after", after);
                    cmd.AddBound("@max", _pageSize);
                    await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var consent = ReadConsent(reader);
                        if (consent.TenantId == tenant)
                        {
                            result.Add(consent);
                        }

                        after = reader.GetString(0);
                        page++;
                    }
                }

                if (page < _pageSize)
                {
                    break;
                }
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        // Rule rows are written with their consent and never changed afterwards, so they are read outside the snapshot.
        for (var i = 0; i < result.Count; i += ConsentPageSize)
        {
            await ConsentSqlCore.HydrateConsentsBatchAsync(conn, result.GetRange(i, Math.Min(ConsentPageSize, result.Count - i)), ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>PostgreSQL reads one MVCC snapshot; SQL Server uses snapshot isolation when the database allows it.</summary>
    private async Task<IsolationLevel> ReadIsolationAsync(DbConnection conn, CancellationToken ct)
    {
        if (!IsSqlServer)
        {
            return IsolationLevel.RepeatableRead;
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT snapshot_isolation_state FROM sys.databases WHERE name = DB_NAME()";
        var state = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return state is not null && state != DBNull.Value && Convert.ToInt32(state, CultureInfo.InvariantCulture) == 1
            ? IsolationLevel.Snapshot
            : IsolationLevel.ReadCommitted;
    }

    public async Task<IReadOnlyList<UcLedgerEntry>> ListLedgerAsync(string dataSource, CancellationToken ct)
    {
        var source = RequireDataSource(dataSource);
        await using var conn = await _repository.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT " + LedgerColumns + " FROM " + Ledger + " WHERE data_source = @dataSource ORDER BY securable_type, full_name, principal_external_id, privilege";
        cmd.AddBound("@dataSource", source);
        var result = new List<UcLedgerEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadLedger(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> ListLedgerDataSourcesAsync(CancellationToken ct)
    {
        await using var conn = await _repository.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT data_source FROM " + Ledger + " ORDER BY data_source";
        var result = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public Task SaveLedgerAsync(string dataSource, long fencingToken, IReadOnlyList<UcLedgerEntry> entries, IReadOnlyList<AuditLogEntry> audit, CancellationToken ct)
    {
        var (source, ordered) = PrepareLedger(dataSource, entries, audit);
        return ordered.Count == 0 && audit.Count == 0
            ? Task.CompletedTask
            : _repository.WriteWithAuditAsync(
                async (conn, tx) =>
                {
                    await FenceAsync(conn, tx, source, fencingToken, ct).ConfigureAwait(false);
                    foreach (var entry in ordered)
                    {
                        await UpsertLedgerAsync(conn, tx, entry, ct).ConfigureAwait(false);
                    }

                    return true;
                },
                static applied => applied,
                audit,
                ct);
    }

    public Task DeleteLedgerAsync(string dataSource, long fencingToken, IReadOnlyList<UcLedgerEntry> entries, IReadOnlyList<AuditLogEntry> audit, CancellationToken ct)
    {
        var (source, ordered) = PrepareLedger(dataSource, entries, audit);
        return ordered.Count == 0 && audit.Count == 0
            ? Task.CompletedTask
            : _repository.WriteWithAuditAsync(
                async (conn, tx) =>
                {
                    await FenceAsync(conn, tx, source, fencingToken, ct).ConfigureAwait(false);
                    foreach (var entry in ordered)
                    {
                        await using var cmd = Command(conn, tx, "DELETE FROM " + Ledger + LedgerKeyWhere);
                        BindKey(cmd, entry);
                        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }

                    return true;
                },
                static applied => applied,
                audit,
                ct);
    }

    /// <summary>Validates and normalizes a ledger change set; rows are written in key order so two writers never cross.</summary>
    private static (string Source, List<UcLedgerEntry> Ordered) PrepareLedger(string dataSource, IReadOnlyList<UcLedgerEntry> entries, IReadOnlyList<AuditLogEntry> audit)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(audit);
        var source = RequireDataSource(dataSource);
        var ordered = new List<UcLedgerEntry>(entries.Count);
        foreach (var entry in entries)
        {
            Validate(entry);
            if (!string.Equals(entry.DataSource.ToLowerInvariant(), source, StringComparison.Ordinal))
            {
                throw new ArgumentException("Every ledger entry of a change set must belong to its data source.", nameof(entries));
            }

            ordered.Add(entry with { DataSource = source });
        }

        ordered.Sort(static (a, b) => string.CompareOrdinal(KeyText(a), KeyText(b)));
        return (source, ordered);
    }

    private static string KeyText(UcLedgerEntry e) =>
        e.SecurableType.ToStored() + "|" + e.FullName + "|" + e.PrincipalExternalId + "|" + e.Privilege;

    public async Task<IReadOnlyList<UcGrantFeedback>> ListFeedbackAsync(TenantId tenant, string dataSource, CancellationToken ct)
    {
        var tenantValue = RequireTenant(tenant);
        var source = RequireDataSource(dataSource);
        await using var conn = await _repository.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT " + FeedbackColumns + " FROM " + Feedback + " WHERE tenant_id = @tenantId AND data_source = @dataSource ORDER BY consent_id";
        cmd.AddBound("@tenantId", tenantValue);
        cmd.AddBound("@dataSource", source);
        var result = new List<UcGrantFeedback>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(ReadFeedback(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<TenantId>> ListFeedbackTenantsAsync(string dataSource, CancellationToken ct)
    {
        var source = RequireDataSource(dataSource);
        await using var conn = await _repository.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT tenant_id FROM " + Feedback + " WHERE data_source = @dataSource AND state <> @revoked ORDER BY tenant_id";
        cmd.AddBound("@dataSource", source);
        cmd.AddBound("@revoked", UcGrantState.Revoked.ToStored());
        var result = new List<TenantId>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // A stored tenant that is not a valid, writable tenant is never acted on.
            if (TenantId.TryParse(reader.GetString(0), out var tenant) && !TenantId.IsReserved(tenant))
            {
                result.Add(tenant);
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, UcGrantFeedback>> GetFeedbackAsync(TenantId tenant, IReadOnlyCollection<Guid> consentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(consentIds);
        var tenantValue = RequireTenant(tenant);
        var result = new Dictionary<Guid, UcGrantFeedback>();
        if (consentIds.Count == 0)
        {
            return result;
        }

        await using var conn = await _repository.OpenConnectionAsync(ct).ConfigureAwait(false);
        foreach (var batch in consentIds.Distinct().Select(id => id.ToString()).Chunk(ConsentPageSize))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT " + FeedbackColumns + " FROM " + Feedback + " WHERE tenant_id = @tenantId AND consent_id IN (" +
                              ConsentSqlCore.BindIdInClause(cmd, "cid", batch) + ")";
            cmd.AddBound("@tenantId", tenantValue);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = ReadFeedback(reader);
                result[row.ConsentId] = row;
            }
        }

        return result;
    }

    public Task SaveFeedbackAsync(
        TenantId tenant, string dataSource, long fencingToken, IReadOnlyList<UcGrantFeedback> rows, IReadOnlyList<AuditLogEntry> audit,
        IReadOnlyList<UcFeedbackEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(events);
        var tenantValue = RequireTenant(tenant);
        var source = RequireDataSource(dataSource);
        if (rows.Any(r => r.Tenant != tenant) || audit.Any(a => a.TenantId != tenant))
        {
            throw new ArgumentException("Every feedback row and audit entry must belong to the tenant.", nameof(rows));
        }

        foreach (var row in rows)
        {
            if (!string.Equals(RequireDataSource(row.DataSource), source, StringComparison.Ordinal))
            {
                throw new ArgumentException("Every feedback row must belong to the data source.", nameof(rows));
            }

            _ = row.State.ToStored();
            if (row.ReasonCode is { Length: > 64 })
            {
                throw new ArgumentException("A reason code is at most 64 characters.", nameof(rows));
            }
        }

        if (events.Any(e => !rows.Any(r => r.ConsentId == e.ConsentId)))
        {
            throw new ArgumentException("Every feedback event must belong to a written feedback row.", nameof(events));
        }

        async Task WriteAsync(DbConnection conn, DbTransaction tx)
        {
            await FenceAsync(conn, tx, source, fencingToken, ct).ConfigureAwait(false);
            foreach (var row in rows.OrderBy(r => r.ConsentId))
            {
                await UpsertFeedbackAsync(conn, tx, tenantValue, row with { DataSource = source }, ct).ConfigureAwait(false);
            }
        }

        if (rows.Count == 0 && audit.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (events.Count == 0)
        {
            return _repository.WriteWithAuditAsync(
                async (conn, tx) =>
                {
                    await WriteAsync(conn, tx).ConfigureAwait(false);
                    return true;
                },
                static applied => applied,
                audit,
                ct);
        }

        var writes = events
            .Select(e => new UcProvisioningEventWrite(
                e.Provisioned ? ConsentEventTypes.GrantProvisioned : ConsentEventTypes.GrantProvisioningFailed,
                e.Provisioned ? "PROVISIONED" : "PROVISIONING_FAILED",
                e.ConsentId,
                e.Table,
                e.OccurredAt))
            .ToList();
        return _repository.WriteWithAuditAndConsentEventsAsync(WriteAsync, audit, tenant, writes, ct);
    }

    // ---- writes ----

    /// <summary>
    /// The fence row is the first lock of every write transaction. A token lower than the highest one seen for the data source means
    /// the writer lost its lease to a newer one: the transaction is rejected.
    /// </summary>
    private async Task FenceAsync(DbConnection conn, DbTransaction tx, string source, long fencingToken, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        await using (var update = Command(conn, tx, "UPDATE " + Fence + FenceUpdate))
        {
            BindFence(update, source, fencingToken, now);
            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0)
            {
                return;
            }
        }

        await using (var exists = Command(conn, tx, "SELECT COUNT(*) FROM " + Fence + FenceExists))
        {
            exists.AddBound("@fenceSource", source);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
            {
                throw new Autheris.Application.State.FencingTokenRejectedException("uc-provisioning:" + source, fencingToken);
            }
        }

        await using var insert = Command(conn, tx, "INSERT INTO " + Fence + FenceInsert);
        BindFence(insert, source, fencingToken, now);
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void BindFence(DbCommand cmd, string source, long fencingToken, DateTimeOffset now)
    {
        cmd.AddBound("@fenceSource", source);
        cmd.AddBound("@fencingToken", fencingToken);
        cmd.AddBound("@fencedAt", now, DbType.DateTimeOffset);
    }

    private async Task UpsertLedgerAsync(DbConnection conn, DbTransaction tx, UcLedgerEntry entry, CancellationToken ct)
    {
        await using (var update = Command(conn, tx, "UPDATE " + Ledger + LedgerUpdateSet + LedgerKeyWhere))
        {
            BindKey(update, entry);
            BindLedgerValues(update, entry);
            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0)
            {
                return;
            }
        }

        await using var insert = Command(conn, tx, "INSERT INTO " + Ledger + LedgerInsertValues);
        BindKey(insert, entry);
        BindLedgerValues(insert, entry);
        insert.AddBound("@id", entry.Id.ToString("D"));
        insert.AddBound("@createdAt", entry.CreatedAt.ToUniversalTime(), DbType.DateTimeOffset);
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task UpsertFeedbackAsync(DbConnection conn, DbTransaction tx, string tenant, UcGrantFeedback row, CancellationToken ct)
    {
        await using (var update = Command(conn, tx, "UPDATE " + Feedback + FeedbackUpdateSet))
        {
            BindFeedback(update, tenant, row);
            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0)
            {
                return;
            }
        }

        await using var insert = Command(conn, tx, "INSERT INTO " + Feedback + FeedbackInsertValues);
        BindFeedback(insert, tenant, row);
        await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static DbCommand Command(DbConnection conn, DbTransaction tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static void BindKey(DbCommand cmd, UcLedgerEntry entry)
    {
        cmd.AddBound("@dataSource", entry.DataSource);
        cmd.AddBound("@securableType", entry.SecurableType.ToStored());
        cmd.AddBound("@fullName", entry.FullName);
        cmd.AddBound("@principalExternalId", entry.PrincipalExternalId);
        cmd.AddBound("@privilege", entry.Privilege);
    }

    private static void BindLedgerValues(DbCommand cmd, UcLedgerEntry entry)
    {
        cmd.AddBound("@principal", entry.Principal);
        cmd.AddBound("@state", entry.State.ToStored());
        cmd.AddBound("@attempts", entry.Attempts);
        cmd.AddBound("@nextAttemptAt", Timestamp(entry.NextAttemptAt), DbType.DateTimeOffset);
        cmd.AddBound("@lastErrorCode", entry.LastErrorCode is null ? DBNull.Value : entry.LastErrorCode);
        cmd.AddBound("@appliedAt", Timestamp(entry.AppliedAt), DbType.DateTimeOffset);
        cmd.AddBound("@verifiedAt", Timestamp(entry.VerifiedAt), DbType.DateTimeOffset);
        cmd.AddBound("@updatedAt", entry.UpdatedAt.ToUniversalTime(), DbType.DateTimeOffset);
    }

    private static void BindFeedback(DbCommand cmd, string tenant, UcGrantFeedback row)
    {
        cmd.AddBound("@tenantId", tenant);
        cmd.AddBound("@consentId", row.ConsentId.ToString("D"));
        cmd.AddBound("@dataSource", row.DataSource);
        cmd.AddBound("@state", row.State.ToStored());
        cmd.AddBound("@reasonCode", row.ReasonCode is null ? DBNull.Value : row.ReasonCode);
        cmd.AddBound("@updatedAt", row.UpdatedAt.ToUniversalTime(), DbType.DateTimeOffset);
        cmd.AddBound("@verifiedAt", Timestamp(row.VerifiedAt), DbType.DateTimeOffset);
    }

    private static object Timestamp(DateTimeOffset? value) => value is { } v ? v.ToUniversalTime() : DBNull.Value;

    // ---- reads ----

    private static Consent ReadConsent(DbDataReader reader)
    {
        var granteeSid = reader.IsDBNull(5) ? null : reader.GetString(5);
        return ConsentWorkflowSql.BuildConsent(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            new TableIdentifier(reader.GetString(9), reader.GetString(10), reader.GetString(11)),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            Enum.Parse<ConsentEffect>(reader.GetString(3), true),
            GranteeTypeParsing.ParseStored(reader.GetString(4)),
            granteeSid is null ? (Sid?)null : new Sid(granteeSid),
            DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
            reader.GetInt32(8) == 1,
            StoredTenant.Parse(reader.IsDBNull(12) ? null : reader.GetString(12)),
            reader,
            14,
            renewalCount: reader.GetInt32(13));
    }

    /// <summary>A row with an unknown securable type fails the read: it can neither be revoked nor silently ignored.</summary>
    private static UcLedgerEntry ReadLedger(DbDataReader reader)
    {
        if (!UcProvisioningStoreNames.TryParseSecurableType(reader.GetString(2), out var type))
        {
            throw new InvalidOperationException("A provisioning ledger row has an unknown securable type.");
        }

        return new UcLedgerEntry
        {
            Id = Guid.Parse(reader.GetString(0)),
            DataSource = reader.GetString(1),
            SecurableType = type,
            FullName = reader.GetString(3),
            Principal = reader.GetString(4),
            PrincipalExternalId = reader.GetString(5),
            Privilege = reader.GetString(6),
            State = UcProvisioningStoreNames.ParseLedgerState(reader.GetString(7)),
            Attempts = reader.GetInt32(8),
            NextAttemptAt = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 9),
            LastErrorCode = ConsentWorkflowSql.ReadOptionalString(reader, 10),
            AppliedAt = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 11),
            VerifiedAt = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 12),
            CreatedAt = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 13) ?? DateTimeOffset.UnixEpoch,
            UpdatedAt = ConsentWorkflowSql.ReadOptionalTimestamp(reader, 14) ?? DateTimeOffset.UnixEpoch
        };
    }

    private static UcGrantFeedback ReadFeedback(DbDataReader reader) => new(
        StoredTenant.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)),
        reader.GetString(2),
        UcProvisioningStoreNames.ParseGrantState(reader.GetString(3)),
        ConsentWorkflowSql.ReadOptionalString(reader, 4),
        ConsentWorkflowSql.ReadOptionalTimestamp(reader, 5) ?? DateTimeOffset.UnixEpoch,
        ConsentWorkflowSql.ReadOptionalTimestamp(reader, 6));

    // ---- guards ----

    private static void Validate(UcLedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        RequireDataSource(entry.DataSource);
        _ = entry.SecurableType.ToStored();
        _ = entry.State.ToStored();
        if (string.IsNullOrWhiteSpace(entry.FullName) || entry.FullName.Length > UcSecurableNames.MaxFullNameLength ||
            entry.FullName.Split('.').Any(segment => !UcSecurableNames.IsValidSegment(segment)) ||
            string.IsNullOrWhiteSpace(entry.Principal) || entry.Principal.Length > 256 ||
            string.IsNullOrWhiteSpace(entry.PrincipalExternalId) || entry.PrincipalExternalId.Length > 64 ||
            !string.Equals(entry.PrincipalExternalId, entry.PrincipalExternalId.ToLowerInvariant(), StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(entry.Privilege) || entry.Privilege.Length > 32 ||
            entry.LastErrorCode is { Length: > 64 } ||
            entry.CreatedAt == default || entry.UpdatedAt == default)
        {
            throw new ArgumentException("The ledger entry has an empty, over-long or malformed field.", nameof(entry));
        }
    }

    /// <summary>Data source names are stored and compared in lower case on both engines.</summary>
    private static string RequireDataSource(string? dataSource) =>
        !string.IsNullOrWhiteSpace(dataSource) && dataSource.Length <= 128
            ? dataSource.Trim().ToLowerInvariant()
            : throw new ArgumentException("A data source name of 1 to 128 characters is required.", nameof(dataSource));

    private static string RequireTenant(TenantId tenant) =>
        TenantId.TryParse(TenantScope.RequireTenant(tenant, GovernanceTable.Consents.ToSqlName()).Value, out var valid)
            ? valid.Value
            : throw new ArgumentException("A valid non-reserved TenantId is required.", nameof(tenant));
}
