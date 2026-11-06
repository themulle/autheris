using System.Data;
using System.Text.Json;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Npgsql;

namespace Autheris.Infrastructure.Persistence;

public partial class PostgreSqlGovernanceRepository
{
    /// <summary>
    /// Test seam: runs inside the decision transaction between the status read under the lock and the guarded
    /// status UPDATE, so a test can change the status where no concurrent writer can be injected. Always null in production.
    /// </summary>
    internal Func<System.Data.Common.DbConnection, System.Data.Common.DbTransaction, CancellationToken, Task>? ConsentStatusUpdateSeam { get; set; }

    /// <summary>
    /// prefilter on the indexed upper-invariant grantee SID (<c>= ANY(array)</c>). Role consents and rows without a
    /// normalised SID always pass; the C# filter on the loaded rows stays authoritative.
    /// </summary>
    internal const string SubjectPredicate =
        " AND (c.grantee_type = 'Role' OR c.grantee_sid_norm IS NULL OR c.grantee_sid_norm = ANY(@subjects))";

    internal static class ConsentSql
    {
        public const string FindTableIdSql =
            "SELECT id FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table) LIMIT 1";

        public const string SelectExpiringConsentsSql = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                   c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                   c.is_revoked, t.source_name, t.schema_name, t.table_name,
                                   c.tenant_id, c.renewal_count
                            FROM CONSENTS c
                            JOIN TABLES t ON c.table_id = t.id
                            WHERE c.is_revoked = 0 AND c.valid_to > @now AND c.valid_to <= @threshold
                            ORDER BY c.valid_to
                            LIMIT @maxResults";

        public const string SelectExtendTargetSql = @"SELECT t.source_name, t.schema_name, t.table_name, c.tenant_id, c.valid_from
                                       FROM CONSENTS c
                                       JOIN TABLES t ON c.table_id = t.id
                                       WHERE c.id = @id
                                       FOR UPDATE OF c";

        public const string SelectConsentRequestByIdSql = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                                       r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                                       t.source_name, t.schema_name, t.table_name, r.itsm_ticket_id, r.tenant_id,
                                       r.requester_identifiers_json, r.context_json
                                FROM CONSENT_REQUESTS r
                                JOIN TABLES t ON r.table_id = t.id
                                WHERE r.id = @id";

        public const string SelectRequestsByRequesterBaseSql = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                           r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                           t.source_name, t.schema_name, t.table_name, r.context_json
                    FROM CONSENT_REQUESTS r
                    JOIN TABLES t ON r.table_id = t.id
                    WHERE r.requester_sid = @reqSid";

        public const string SelectPendingRequestsForApproverSql = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                                   r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                                   t.source_name, t.schema_name, t.table_name, r.context_json
                            FROM CONSENT_REQUESTS r
                            JOIN TABLES t ON r.table_id = t.id
                            WHERE r.status IN ('PENDING', 'PENDING_SECOND_APPROVAL')
                              AND r.table_id IN (
                                  SELECT tow.table_id
                                  FROM TABLE_OWNERS tow
                                  JOIN DATA_OWNERS o ON tow.data_owner_id = o.id
                                  WHERE o.ad_sid = @apprSid AND o.is_active = 1
                                  UNION
                                  SELECT tow.table_id
                                  FROM DATA_OWNER_DELEGATIONS del
                                  JOIN TABLE_OWNERS tow ON del.data_owner_id = tow.data_owner_id
                                  WHERE del.delegate_sid = @apprSid AND del.valid_from <= @now AND @now < del.valid_to
                              )";

        public const string SelectRequestStatusForUpdateSql = "SELECT status FROM CONSENT_REQUESTS WHERE id = @id FOR UPDATE";

        public const string CountConfiguredOwnersSql = @"SELECT COUNT(*)
                            FROM TABLES t
                            JOIN TABLE_OWNERS tow ON t.id = tow.table_id
                            WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table)";

        public const string SelectRequestIdByTicketSql =
            "SELECT id FROM CONSENT_REQUESTS WHERE itsm_ticket_id = @ticket ORDER BY requested_at, id LIMIT 1";

        public const string SelectRequestIdByTicketAndTenantSql =
            "SELECT id FROM CONSENT_REQUESTS WHERE itsm_ticket_id = @ticket AND tenant_id = @tenant ORDER BY requested_at, id LIMIT 1";

        public const string SelectSystemRevokeTargetSql = @"SELECT t.source_name, t.schema_name, t.table_name, c.tenant_id
                                       FROM CONSENTS c
                                       JOIN TABLES t ON c.table_id = t.id
                                       WHERE c.id = @id AND c.consent_request_id = @reqId AND c.is_revoked = 0
                                       FOR UPDATE";

        public const string UpdateSystemRevokeSql = @"UPDATE CONSENTS
                                          SET is_revoked = 1, revoked_by_sid = @sid, revoked_at = @now, revoke_reason = @reason
                                          WHERE id = @id";
    }

    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(
        IEnumerable<Sid> subjects,
        TableIdentifier table,
        DateTimeOffset atTime,
        CancellationToken ct = default) =>
        GetActiveConsentsForSubjectsAsync(subjects, table, atTime, null, ct);

    public async Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(
        IEnumerable<Sid> subjects,
        TableIdentifier table,
        DateTimeOffset atTime,
        TenantId? tenantId,
        CancellationToken ct = default)
    {
        var subjectList = subjects.ToList();
        var subjectSet = subjectList.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (subjectSet.Count == 0) return Array.Empty<Consent>();
        var normalized = ConsentSubjectFilter.DistinctNormalized(subjectList);

        var consents = new List<Consent>();
        var seen = new HashSet<Guid>();
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        var tenantFilter = tenantId != null ? " AND c.tenant_id = @tenantId" : "";
        cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                   c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                   c.is_revoked, c.revoked_by_sid, c.revoked_at, c.revoke_reason,
                                   c.tenant_id
                            FROM CONSENTS c
                            JOIN TABLES t ON c.table_id = t.id
                            WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table)
                              AND c.is_revoked = 0" + tenantFilter + SubjectPredicate;
        cmd.Parameters.AddWithValue("@subjects", normalized);
        cmd.Parameters.AddWithValue("@domain", table.Domain);
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table", table.TableName);
        if (tenantId != null)
        {
            cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
        }

        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            ConsentSqlCore.ReadSubjectConsentRows(reader, table, atTime, subjectSet, seen, consents);
        }

        consents.RemoveAll(c => !c.IsActive(atTime) && !(c.Effect == ConsentEffect.Deny && c.ValidFrom > atTime));
        await HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        return consents;
    }

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(
        IEnumerable<Sid> subjects,
        IEnumerable<string>? roles = null,
        DateTimeOffset? atTime = null,
        CancellationToken ct = default) =>
        GetAllActiveConsentsForSubjectsAsync(subjects, roles, atTime, null, ct);

    public async Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(
        IEnumerable<Sid> subjects,
        IEnumerable<string>? roles,
        DateTimeOffset? atTime,
        TenantId? tenantId,
        CancellationToken ct = default)
    {
        var effectiveAt = atTime ?? DateTimeOffset.UtcNow;
        var subjectEnumerated = subjects.ToList();
        var subjectList = subjectEnumerated.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalized = ConsentSubjectFilter.DistinctNormalized(subjectEnumerated);
        var roleSet = roles != null
            ? new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var consents = new List<Consent>();
        var seen = new HashSet<Guid>();
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        var tenantFilter = tenantId != null ? " AND c.tenant_id = @tenantId" : "";
        cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                   c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                   c.is_revoked, t.source_name, t.schema_name, t.table_name,
                                   c.tenant_id
                            FROM CONSENTS c
                            JOIN TABLES t ON c.table_id = t.id
                            WHERE c.is_revoked = 0" + tenantFilter + SubjectPredicate;
        cmd.Parameters.AddWithValue("@subjects", normalized);
        if (tenantId != null)
        {
            cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
        }

        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            ConsentSqlCore.ReadAllSubjectConsentRows(reader, effectiveAt, subjectList, roleSet, seen, consents);
        }

        consents.RemoveAll(c => !c.IsActive(effectiveAt));
        await HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        return consents;
    }

    public async Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(
        Guid consentRequestId,
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var consents = await ConsentSqlCore.ReadActiveConsentsByRequestIdAsync(conn, consentRequestId, atTime, includeNotYetValid: false, ct).ConfigureAwait(false);
        await HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        return consents;
    }

    /// <summary>opt-in load of<c>CONSENTS.description</c> (<see cref="IConsentDescriptionRepository.LoadDescriptionsAsync"/>); never part of hydration.</summary>
    public async Task LoadDescriptionsAsync(IReadOnlyCollection<Consent> consents, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consents);
        if (consents.Count == 0) return;
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await ConsentSqlCore.LoadConsentDescriptionsAsync(conn, consents, ct).ConfigureAwait(false);
    }

    private Task HydrateConsentsBatchAsync(NpgsqlConnection conn, List<Consent> consents, CancellationToken ct) =>
        ConsentSqlCore.HydrateConsentsBatchAsync(conn, consents, ct);

    public async Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default)
    {
        StoredTenant.RequireWritable(consent.TenantId);
        ArgumentNullException.ThrowIfNull(consent);
        consent.Validate();
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var created = await InsertConsentCoreAsync(conn, tx, consent, ct).ConfigureAwait(false);

            await IncrementTableEpochInternalAsync(conn, tx, consent.TableIdentifier, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            OnTransactionCommitted(tx);

            _metadataCache.TryRemove(consent.TableIdentifier.ToString().ToLowerInvariant(), out _);
            await _epochValidationService.InvalidateEpochAsync(consent.TableIdentifier, ct).ConfigureAwait(false);

            return created;
        }
        catch
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            OnTransactionRolledBack(tx);
            throw;
        }
    }

    /// <summary>Inserts the consent row, its column rules and row filters and the grant audit entry inside <paramref name="tx"/>. Neither bumps the epoch nor commits.</summary>
    private async Task<Consent> InsertConsentCoreAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Consent consent, CancellationToken ct)
    {
        const string insertConsentSql = @"INSERT INTO CONSENTS (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, grantee_sid_norm, role_id, role_name, valid_from, valid_to, is_revoked, tenant_id, description)
                                VALUES (@id, @tid, @reqId, @effect, @granteeType, @granteeSid, @sidNorm, @roleId, @roleName, @validFrom, @validTo, 0, @tenantId, @description)";

        var (created, auditEntry) = await ConsentSqlCore.InsertConsentRowsAsync(conn, tx, insertConsentSql, ConsentSql.FindTableIdSql, consent, ct).ConfigureAwait(false);
        await RecordAuditEventAsync(auditEntry, tx, ct).ConfigureAwait(false);
        return created;
    }

    public async Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var consent = await ConsentSqlCore.ReadConsentByIdRowAsync(conn, consentId, ct).ConfigureAwait(false);
        if (consent != null)
        {
            await HydrateConsentsBatchAsync(conn, [consent], ct).ConfigureAwait(false);
        }

        return consent;
    }

    public async Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        TableIdentifier? tableId = null;

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var (foundTable, rawTenant) = await ConsentSqlCore.ReadSystemRevokeTargetAsync(
                conn, tx, ConsentSql.SelectSystemRevokeTargetSql, consentId, consentRequestId, ct).ConfigureAwait(false);
            if (!foundTable.HasValue)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            tableId = foundTable;
            var tenantId = ConsentGrantSqlCore.ResolveConsentTenant(rawTenant, consentId, tableId.Value.ToString());
            var now = DateTimeOffset.UtcNow;

            await ConsentSqlCore.ExecuteSystemRevokeUpdateAsync(
                conn, tx, ConsentSql.UpdateSystemRevokeSql, consentId, consentRequestId, revokedBySid, now, reason, bindReqId: false, ct).ConfigureAwait(false);

            await IncrementTableEpochInternalAsync(conn, tx, tableId.Value, ct).ConfigureAwait(false);

            var entry = new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                OccurredAt = now,
                EventType = "CONSENT_REVOKED",
                ActorSid = revokedBySid,
                TargetTable = tableId.Value.ToString(),
                Decision = ConsentGrantSqlCore.RevokedDecision,
                TraceId = Guid.NewGuid().ToString(),
                DetailsJson = JsonSerializer.Serialize(new { ConsentId = consentId, ConsentRequestId = consentRequestId, Reason = reason }),
                TenantId = tenantId
            };
            await RecordAuditEventAsync(entry, tx, ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            OnTransactionCommitted(tx);
        }
        catch (ConsentTenantIntegrityException refusal)
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            OnTransactionRolledBack(tx);
            await RecordTenantRefusalAuditAsync(conn, revokedBySid, refusal, ct).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            OnTransactionRolledBack(tx);
            throw;
        }

        if (tableId.HasValue)
        {
            _metadataCache.TryRemove(tableId.Value.ToString().ToLowerInvariant(), out _);
            await _epochValidationService.InvalidateEpochAsync(tableId.Value, ct).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var consents = await ConsentSqlCore.ReadExpiringConsentRowsAsync(
            conn, ConsentSql.SelectExpiringConsentsSql, DateTimeOffset.UtcNow, threshold, maxResults, ct).ConfigureAwait(false);
        await HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        return consents;
    }

    public async Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default)
    {
        TableIdentifier? tableId;
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            (tableId, var extendTenant, _) = await ConsentSqlCore.ReadAndExtendConsentExpiryAsync(
                conn, tx, ConsentSql.SelectExtendTargetSql, consentId, newValidTo, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

            if (tableId.HasValue)
            {
                await IncrementTableEpochInternalAsync(conn, tx, tableId.Value, ct).ConfigureAwait(false);
            }

            await RecordAuditEventAsync(ConsentSqlCore.BuildExpiryExtendedAudit(extendTenant, tableId, consentId, newValidTo), tx, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            OnTransactionCommitted(tx);
        }
        catch
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            OnTransactionRolledBack(tx);
            throw;
        }

        if (tableId.HasValue)
        {
            _metadataCache.TryRemove(tableId.Value.ToString().ToLowerInvariant(), out _);
            await _epochValidationService.InvalidateEpochAsync(tableId.Value, ct).ConfigureAwait(false);
        }
    }

    public async Task<(ConsentRowFilter Filter, ConsentEffect Effect, TenantId Tenant, TableIdentifier Table)?> GetRowFilterAsync(
        Guid rowFilterId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var row = await RowFilterEvaluationStore.ReadRuleAsync(conn, null, CatalogKeyDefinitionStore.PostgreSql, rowFilterId, ct).ConfigureAwait(false);
        return row is null ? null : (row.Filter, row.Effect, row.Tenant, row.Table);
    }

    public async Task<RowFilterEvaluationChange?> UpdateRowFilterEvaluationAsync(
        Guid rowFilterId, TenantId tenant, RowFilterEvaluationSettings settings, CancellationToken ct = default)
    {
        RowFilterEvaluationChange? change;
        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                change = await RowFilterEvaluationStore.UpdateRuleAsync(
                    conn, tx, CatalogKeyDefinitionStore.PostgreSql, rowFilterId, tenant, settings,
                    table => IncrementTableEpochInternalAsync(conn, tx, table, ct), ct).ConfigureAwait(false);
                if (change is null)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return null;
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }

        _metadataCache.TryRemove(change.TargetTable.ToString().ToLowerInvariant(), out _);
        await _epochValidationService.InvalidateEpochAsync(change.TargetTable, ct).ConfigureAwait(false);
        return change;
    }

    public async Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ConsentSqlCore.CreateConsentRequestAsync(conn, ConsentSql.FindTableIdSql, request, ct).ConfigureAwait(false);
    }

    public async Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ConsentSqlCore.ReadConsentRequestAsync(conn, null, ConsentSql.SelectConsentRequestByIdSql, requestId, loadSteps: true, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, string? status = null, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ConsentSqlCore.GetRequestsByRequesterSidAsync(conn, ConsentSql.SelectRequestsByRequesterBaseSql, requesterSid, status, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ConsentSqlCore.GetPendingRequestsForApproverAsync(conn, ConsentSql.SelectPendingRequestsForApproverSql, approverSid, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
    }

    public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default)
        => ApproveConsentRequestStepAsync(requestId, approverSid, isExternalItsmApproval: false, itsmApproverAccount: null, ct);

    public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, bool isExternalItsmApproval, CancellationToken ct = default)
        => ApproveConsentRequestStepAsync(requestId, approverSid, isExternalItsmApproval, itsmApproverAccount: null, ct);

    public async Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, bool isExternalItsmApproval, string? itsmApproverAccount, CancellationToken ct = default)
    {
        var req = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false);
        if (req == null) throw new InvalidOperationException($"Request {requestId} not found.");

        ConsentApprovalPolicy.EnsureApprovableStatus(requestId, req.Status, isExternalItsmApproval);
        var approverOwnerIds = await EnsureApproverIsNotRequesterAsync(req, approverSid, itsmApproverAccount, ct).ConfigureAwait(false);

        if (!isExternalItsmApproval)
        {
            if (!await IsAuthorizedApproverForTableInternalAsync(req.TableIdentifier, approverSid, null, ct).ConfigureAwait(false))
            {
                throw new UnauthorizedAccessException($"User '{approverSid}' is neither a data owner nor a delegated approver for table '{req.TableIdentifier}'.");
            }
        }
        else if (await HasConfiguredDataOwnersAsync(req.TableIdentifier, ct).ConfigureAwait(false))
        {
            string? account = string.IsNullOrWhiteSpace(itsmApproverAccount) ? null : itsmApproverAccount.Trim();
            if (account == null)
            {
                throw new UnauthorizedAccessException($"ITSM approval without a named approver is not allowed for table '{req.TableIdentifier}' with configured data owners.");
            }

            if (!await IsAuthorizedApproverForTableInternalAsync(req.TableIdentifier, approverSid, account, ct).ConfigureAwait(false))
            {
                throw new UnauthorizedAccessException($"ITSM approver '{account}' is neither a data owner nor a delegated approver for table '{req.TableIdentifier}'.");
            }
        }

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            string currentStatus = await ConsentSqlCore.ReadRequestStatusOrThrowAsync(conn, tx, ConsentSql.SelectRequestStatusForUpdateSql, requestId, ct).ConfigureAwait(false);
            ConsentApprovalPolicy.EnsureApprovableStatus(requestId, currentStatus, isExternalItsmApproval);

            var state = await ReadLockedApprovalStateAsync(conn, tx, requestId, approverSid, itsmApproverAccount, approverOwnerIds, ct).ConfigureAwait(false);
            if (state.SameApprover)
            {
                if (isExternalItsmApproval)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return req;
                }

                throw new InvalidOperationException(ConsentApprovalPolicy.SameApproverViolationMessage);
            }

            int stepNumber = state.NextStepNumber;
            await ConsentSqlCore.InsertApprovedStepAsync(conn, tx, Guid.NewGuid(), requestId, stepNumber, approverSid, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

            string newStatus = ConsentApprovalPolicy.StatusAfterApproval(state.RequiresFourEyes, stepNumber, isExternalItsmApproval, state.Sensitivity);
            await ConsentSqlCore.ApplyGuardedStatusAsync(conn, tx, ConsentStatusUpdateSeam, requestId, newStatus, currentStatus, ct).ConfigureAwait(false);

            req.Status = newStatus;
            var auditEntry = ConsentApprovalPolicy.BuildStepAudit(req, approverSid, "CONSENT_APPROVAL_STEP", "APPROVED", newStatus, itsmApproverAccount, null);
            await RecordAuditEventAsync(auditEntry, tx, ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            OnTransactionCommitted(tx);
            return req;
        }
        catch
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            OnTransactionRolledBack(tx);
            throw;
        }
    }

    public async Task<bool> WithdrawConsentRequestAsync(Guid requestId, Sid requesterSid, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await ConsentSqlCore.ExecuteWithdrawUpdateAsync(conn, tx, requestId, requesterSid, ct).ConfigureAwait(false))
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            var req = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false);
            if (req != null)
            {
                req.Status = "WITHDRAWN";
                await RecordAuditEventAsync(
                    ConsentApprovalPolicy.BuildStepAudit(req, requesterSid, AuditEventTypes.ConsentWithdrawn, "WITHDRAWN", "WITHDRAWN", null, "Access request withdrawn by requester."),
                    tx, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            OnTransactionCommitted(tx);
            return true;
        }
        catch
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            OnTransactionRolledBack(tx);
            throw;
        }
    }

    public async Task<Consent> ExecuteAtomicApprovalWithConsentAsync(
        Guid requestId,
        ApprovalStep step,
        Consent consent,
        IReadOnlyList<ConsentRowFilter> rowFilters,
        IReadOnlyList<ConsentColumnRule> columnRules,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(consent);

        consent.RowFilters = rowFilters ?? Array.Empty<ConsentRowFilter>();
        consent.ColumnRules = columnRules ?? Array.Empty<ConsentColumnRule>();
        consent.Validate();
        await EnsureMayMaterializeAsync(consent.TenantId, consent.TableIdentifier, consent.RowFilters, ct).ConfigureAwait(false);

        var req = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false);
        if (req == null) throw new InvalidOperationException($"Request {requestId} not found.");

        var approverOwnerIds = await EnsureApproverIsNotRequesterAsync(req, step.ApproverSid, null, ct).ConfigureAwait(false);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            string currentStatus = await ConsentSqlCore.ReadRequestStatusOrThrowAsync(conn, tx, ConsentSql.SelectRequestStatusForUpdateSql, requestId, ct).ConfigureAwait(false);
            ConsentApprovalPolicy.EnsureApprovableStatus(requestId, currentStatus, isExternalItsmApproval: false);

            var state = await ReadLockedApprovalStateAsync(conn, tx, requestId, step.ApproverSid, null, approverOwnerIds, ct).ConfigureAwait(false);
            if (state.SameApprover)
            {
                throw new InvalidOperationException(ConsentApprovalPolicy.SameApproverViolationMessage);
            }

            string newStatus = ConsentApprovalPolicy.StatusAfterApproval(state.RequiresFourEyes, state.NextStepNumber, isExternalItsmApproval: false, state.Sensitivity);
            if (!string.Equals(newStatus, ConsentApprovalPolicy.Approved, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(ConsentApprovalPolicy.FourEyesNotSatisfiedMessage(requestId));
            }

            await ConsentSqlCore.InsertApprovedStepAsync(conn, tx, step.Id, requestId, state.NextStepNumber, step.ApproverSid, step.DecidedAt ?? DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            await ConsentSqlCore.ApplyGuardedStatusAsync(conn, tx, ConsentStatusUpdateSeam, requestId, newStatus, currentStatus, ct).ConfigureAwait(false);

            req.Status = newStatus;
            await RecordAuditEventAsync(
                ConsentApprovalPolicy.BuildStepAudit(req, step.ApproverSid, "CONSENT_APPROVAL_STEP", "APPROVED", newStatus, null, null),
                tx, ct).ConfigureAwait(false);

            await InsertConsentCoreAsync(conn, tx, consent, ct).ConfigureAwait(false);
            await IncrementTableEpochInternalAsync(conn, tx, consent.TableIdentifier, ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            OnTransactionCommitted(tx);
            await _epochValidationService.InvalidateEpochAsync(consent.TableIdentifier, ct).ConfigureAwait(false);

            return consent;
        }
        catch
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            OnTransactionRolledBack(tx);
            throw;
        }
    }

    private async Task<IReadOnlyCollection<string>> EnsureApproverIsNotRequesterAsync(ConsentRequest req, Sid approverSid, string? itsmApproverAccount, CancellationToken ct)
    {
        if (ConsentApprovalPolicy.IsSelfApproval(req, approverSid, itsmApproverAccount))
        {
            throw new InvalidOperationException("Separation of duties violated: The requester cannot approve their own request.");
        }

        var approverOwnerIds = await ResolveDataOwnerIdsInternalAsync(ConsentApprovalPolicy.IdentifierCandidates(approverSid.Value, itsmApproverAccount), ct).ConfigureAwait(false);
        if (ConsentApprovalPolicy.ShareOwnerId(approverOwnerIds, await ResolveRequesterOwnerIdsInternalAsync(req, ct).ConfigureAwait(false)))
        {
            throw new InvalidOperationException("Separation of duties violated: The requester cannot approve their own request.");
        }

        return approverOwnerIds;
    }

    private Task<(bool RequiresFourEyes, string? Sensitivity, bool SameApprover, int NextStepNumber)> ReadLockedApprovalStateAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction tx,
        Guid requestId,
        Sid approverSid,
        string? itsmApproverAccount,
        IReadOnlyCollection<string> approverOwnerIds,
        CancellationToken ct) =>
        ConsentSqlCore.ReadApprovalStateAsync(conn, tx, requestId, approverSid, itsmApproverAccount, approverOwnerIds, ResolveDataOwnerIdsInternalAsync, ct);

    private async Task<bool> HasConfiguredDataOwnersAsync(TableIdentifier table, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ConsentSqlCore.HasConfiguredDataOwnersAsync(conn, ConsentSql.CountConfiguredOwnersSql, table, ct).ConfigureAwait(false);
    }

    public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) =>
        RejectConsentRequestAsync(requestId, approverSid, reason, isExternalItsm: false, ct);

    public async Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, bool isExternalItsm, CancellationToken ct = default)
    {
        var req = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false);
        if (req == null) throw new InvalidOperationException($"Request {requestId} not found.");

        if (!ConsentApprovalPolicy.IsRejectableStatus(req.Status))
        {
            throw new Autheris.Application.Governance.ConsentRequests.ConsentRequestStatusConflictException(requestId);
        }

        bool isAuthorized = isExternalItsm ||
                            await IsAuthorizedApproverForTableInternalAsync(req.TableIdentifier, approverSid, null, ct).ConfigureAwait(false);
        if (!isAuthorized)
        {
            throw new UnauthorizedAccessException($"User '{approverSid}' is neither a data owner nor a delegated approver for table '{req.TableIdentifier}'.");
        }

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            string currentStatus = await ConsentSqlCore.ReadRequestStatusOrThrowAsync(conn, tx, ConsentSql.SelectRequestStatusForUpdateSql, requestId, ct).ConfigureAwait(false);
            if (!ConsentApprovalPolicy.IsRejectableStatus(currentStatus))
            {
                throw new Autheris.Application.Governance.ConsentRequests.ConsentRequestStatusConflictException(requestId);
            }

            await ConsentSqlCore.InsertRejectedStepAsync(conn, tx, requestId, approverSid, reason, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            await ConsentSqlCore.ApplyGuardedStatusAsync(conn, tx, ConsentStatusUpdateSeam, requestId, "REJECTED", currentStatus, ct).ConfigureAwait(false);

            req.Status = "REJECTED";
            var auditEntry = ConsentApprovalPolicy.BuildStepAudit(req, approverSid, "CONSENT_REQUEST_REJECTED", "REJECTED", "REJECTED", null, reason);
            await RecordAuditEventAsync(auditEntry, tx, ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            OnTransactionCommitted(tx);
            return req;
        }
        catch
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            OnTransactionRolledBack(tx);
            throw;
        }
    }

    public async Task<ConsentRequest?> GetConsentRequestByTicketIdAsync(string ticketId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var id = await ConsentSqlCore.FindRequestIdByTicketIdAsync(conn, ConsentSql.SelectRequestIdByTicketSql, ticketId, null, ct).ConfigureAwait(false);
        return id == null ? null : await GetConsentRequestAsync(id.Value, ct).ConfigureAwait(false);
    }

    /// <summary>SR-M-24: tenant-bound ticket lookup in SQL - a ticket id of another tenant never shadows or resolves.</summary>
    public async Task<ConsentRequest?> GetConsentRequestByTicketIdAsync(string ticketId, TenantId tenantId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var id = await ConsentSqlCore.FindRequestIdByTicketIdAsync(conn, ConsentSql.SelectRequestIdByTicketAndTenantSql, ticketId, tenantId, ct).ConfigureAwait(false);
        if (id == null) return null;
        var request = await GetConsentRequestAsync(id.Value, ct).ConfigureAwait(false);
        return request != null && request.TenantId == tenantId ? request : null;
    }

    public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) =>
        ActivateConsentAsync(requestId, null, ct);

    public Task ActivateConsentAsync(Guid requestId, Sid? approvedBy, CancellationToken ct = default) =>
        ActivateConsentCoreAsync(requestId, approvedBy, allowPendingAutoApprove: false, ct);

    /// <summary>Insecure getting-started auto-approve path only: activates a request that is still pending.</summary>
    public Task ActivateConsentForAutoApproveAsync(Guid requestId, CancellationToken ct = default) =>
        ActivateConsentCoreAsync(requestId, null, allowPendingAutoApprove: true, ct);

    private ValueTask EnsureMayMaterializeAsync(TenantId tenant, TableIdentifier table, IReadOnlyList<ConsentRowFilter> filters, CancellationToken ct) =>
        _materializationGuard is null
            ? ValueTask.CompletedTask
            : _materializationGuard.EnsureMayMaterializeAsync(tenant, table, filters, ct);

    private async Task ActivateConsentCoreAsync(Guid requestId, Sid? approvedBy, bool allowPendingAutoApprove, CancellationToken ct)
    {
        var req = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false);
        if (req == null) throw new InvalidOperationException($"ConsentRequest {requestId} not found.");
        await EnsureMayMaterializeAsync(req.TenantId, req.TableIdentifier, Array.Empty<ConsentRowFilter>(), ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var (activated, _) = await ConsentSqlCore.TryActivateConsentRowsAsync(conn, tx, req, allowPendingAutoApprove, "ALLOW", now, ct).ConfigureAwait(false);
            if (!activated)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return;
            }

            await IncrementTableEpochInternalAsync(conn, tx, req.TableIdentifier, ct).ConfigureAwait(false);

            var auditEntry = new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                OccurredAt = now,
                EventType = "CONSENT_GRANTED",
                ActorSid = approvedBy ?? req.RequesterSid,
                TargetTable = req.TableIdentifier.ToString(),
                Decision = "ALLOW",
                TraceId = Guid.NewGuid().ToString(),
                DetailsJson = JsonSerializer.Serialize(new { RequestId = requestId, Grantee = req.RequestedGranteeRef }),
                TenantId = req.TenantId
            };
            await RecordAuditEventAsync(auditEntry, tx, ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            OnTransactionCommitted(tx);
        }
        catch
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            OnTransactionRolledBack(tx);
            throw;
        }

        _metadataCache.TryRemove(req.TableIdentifier.ToString().ToLowerInvariant(), out _);
        await _epochValidationService.InvalidateEpochAsync(req.TableIdentifier, ct).ConfigureAwait(false);
    }

    public async Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await ConsentSqlCore.DeleteConsentRequestRowsAsync(conn, tx, requestId, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task UpdateConsentRequestTicketIdAsync(Guid requestId, string ticketId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await ConsentSqlCore.UpdateConsentRequestTicketIdAsync(conn, requestId, ticketId, ct).ConfigureAwait(false);
    }
}
