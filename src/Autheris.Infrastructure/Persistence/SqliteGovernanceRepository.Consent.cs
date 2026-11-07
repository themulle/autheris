using System.Text.Json;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.Data.Sqlite;

namespace Autheris.Infrastructure.Persistence;

public partial class SqliteGovernanceRepository
{
    /// <summary>
    /// Test seam: runs inside the decision transaction between the status read under the lock and the guarded
    /// status UPDATE, so a test can change the status where no concurrent writer can be injected. Always null in production.
    /// </summary>
    internal Func<System.Data.Common.DbConnection, System.Data.Common.DbTransaction, CancellationToken, Task>? ConsentStatusUpdateSeam { get; set; }

    internal static class ConsentSql
    {
        public const string SelectExpiringConsentsSql = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                   c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                   c.is_revoked, t.source_name, t.schema_name, t.table_name,
                                   c.tenant_id, c.renewal_count
                            FROM CONSENTS c
                            JOIN TABLES t ON c.table_id = t.id
                            WHERE c.is_revoked = 0
                              AND c.valid_to > @now
                              AND c.valid_to <= @threshold
                            ORDER BY c.valid_to
                            LIMIT @maxResults";

        public const string SelectExtendTargetSql = @"SELECT t.source_name, t.schema_name, t.table_name, c.tenant_id, c.valid_from
                                    FROM CONSENTS c
                                    JOIN TABLES t ON c.table_id = t.id
                                    WHERE c.id = @id";

        public const string SelectConsentRequestByIdSql = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                                   r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                                   COALESCE(t.source_name, p.domain, 'default'), t.schema_name, t.table_name,
                                   r.itsm_ticket_id, r.tenant_id, r.requester_identifiers_json, r.context_json
                            FROM CONSENT_REQUESTS r
                            JOIN TABLES t ON r.table_id = t.id
                            LEFT JOIN POLICY_EPOCHS p ON t.id = p.table_id
                            WHERE r.id = @id";

        public const string SelectRequestsByRequesterBaseSql = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                               r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                               COALESCE(t.source_name, p.domain, 'default'), t.schema_name, t.table_name, r.context_json
                        FROM CONSENT_REQUESTS r
                        JOIN TABLES t ON r.table_id = t.id
                        LEFT JOIN POLICY_EPOCHS p ON t.id = p.table_id
                        WHERE r.requester_sid = @reqSid";

        public const string SelectPendingRequestsForApproverSql = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                                       r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                                       COALESCE(t.source_name, p.domain, 'default'), t.schema_name, t.table_name, r.context_json
                                FROM CONSENT_REQUESTS r
                                JOIN TABLES t ON r.table_id = t.id
                                LEFT JOIN POLICY_EPOCHS p ON t.id = p.table_id
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

        public const string SelectRequestStatusSql = "SELECT status FROM CONSENT_REQUESTS WHERE id = @id";

        public const string CountConfiguredOwnersSql = @"
            SELECT 1
            FROM TABLE_OWNERS tow
            JOIN TABLES t ON tow.table_id = t.id
            WHERE t.source_name = @domain COLLATE NOCASE AND t.schema_name = @schema COLLATE NOCASE AND t.table_name = @table COLLATE NOCASE
            LIMIT 1;";

        public const string SelectSystemRevokeTargetSql = @"SELECT t.source_name, t.schema_name, t.table_name, c.tenant_id
                                    FROM CONSENTS c
                                    JOIN TABLES t ON c.table_id = t.id
                                    WHERE c.id = @id AND c.consent_request_id = @reqId AND c.is_revoked = 0";

        public const string UpdateSystemRevokeSql = @"UPDATE CONSENTS
                                    SET is_revoked = 1, revoked_by_sid = @sid, revoked_at = @now, revoke_reason = @reason
                                    WHERE id = @id AND consent_request_id = @reqId AND is_revoked = 0";
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

        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        var conn = scope.Connection;
        var consents = new List<Consent>();
        var seen = new HashSet<Guid>();
        foreach (var chunk in ConsentSubjectFilter.Chunks(normalized))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = BuildSubjectConsentSql(chunk.Count, tenantId != null);
            cmd.Parameters.AddWithValue("@domain", table.Domain);
            cmd.Parameters.AddWithValue("@schema", table.Schema);
            cmd.Parameters.AddWithValue("@table", table.TableName);
            if (tenantId != null)
            {
                cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
            }

            for (var i = 0; i < chunk.Count; i++)
            {
                cmd.Parameters.AddWithValue("@s" + i, chunk[i]);
            }

            using var reader = await cmd.ExecuteReaderAsync(ct);
            ConsentSqlCore.ReadSubjectConsentRows(reader, table, atTime, subjectSet, seen, consents);
        }

        consents.RemoveAll(c => !c.IsActive(atTime) && !(c.Effect == ConsentEffect.Deny && c.ValidFrom > atTime));
        await HydrateConsentsBatchAsync(conn, consents, ct);
        return consents;
    }

    internal static string BuildSubjectConsentSql(int subjectCount, bool withTenant)
    {
        var tenantFilter = withTenant ? " AND c.tenant_id = @tenantId" : "";
        return @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                           c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                           c.is_revoked, c.revoked_by_sid, c.revoked_at, c.revoke_reason,
                                           c.tenant_id
                                    FROM CONSENTS c
                                    JOIN TABLES t ON c.table_id = t.id
                                    WHERE t.source_name = @domain COLLATE NOCASE AND t.schema_name = @schema COLLATE NOCASE AND t.table_name = @table COLLATE NOCASE
                                      AND c.is_revoked = 0" + tenantFilter + ConsentSubjectFilter.InListPredicate(subjectCount);
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

        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        var conn = scope.Connection;
        var consents = new List<Consent>();
        var seen = new HashSet<Guid>();
        foreach (var chunk in ConsentSubjectFilter.Chunks(normalized))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = BuildAllConsentsSql(chunk.Count, tenantId != null);
            if (tenantId != null)
            {
                cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
            }

            for (var i = 0; i < chunk.Count; i++)
            {
                cmd.Parameters.AddWithValue("@s" + i, chunk[i]);
            }

            using var reader = await cmd.ExecuteReaderAsync(ct);
            ConsentSqlCore.ReadAllSubjectConsentRows(reader, effectiveAt, subjectList, roleSet, seen, consents);
        }

        consents.RemoveAll(c => !c.IsActive(effectiveAt));
        await HydrateConsentsBatchAsync(conn, consents, ct);
        return consents;
    }

    internal static string BuildAllConsentsSql(int subjectCount, bool withTenant)
    {
        var tenantFilter = withTenant ? " AND c.tenant_id = @tenantId" : "";
        return @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                           c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                           c.is_revoked, t.source_name, t.schema_name, t.table_name,
                                           c.tenant_id
                                    FROM CONSENTS c
                                    JOIN TABLES t ON c.table_id = t.id
                                    WHERE c.is_revoked = 0" + tenantFilter + ConsentSubjectFilter.InListPredicate(subjectCount);
    }

    public async Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(
        Guid consentRequestId,
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        var consents = await ConsentSqlCore.ReadActiveConsentsByRequestIdAsync(scope.Connection, consentRequestId, atTime, includeNotYetValid: false, ct).ConfigureAwait(false);
        await HydrateConsentsBatchAsync(scope.Connection, consents, ct).ConfigureAwait(false);
        return consents;
    }

    public async Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;

        var (tableId, rawTenant) = await ConsentSqlCore.ReadSystemRevokeTargetAsync(
            conn, null, ConsentSql.SelectSystemRevokeTargetSql, consentId, consentRequestId, ct).ConfigureAwait(false);
        if (!tableId.HasValue)
        {
            return false;
        }

        TenantId tenantId;
        try
        {
            tenantId = ConsentGrantSqlCore.ResolveConsentTenant(rawTenant, consentId, tableId.Value.ToString());
        }
        catch (ConsentTenantIntegrityException refusal)
        {
            await RecordTenantRefusalAuditAsync(conn, revokedBySid, refusal, ct).ConfigureAwait(false);
            throw;
        }

        if (await ConsentSqlCore.ExecuteSystemRevokeUpdateAsync(
                conn, null, ConsentSql.UpdateSystemRevokeSql, consentId, consentRequestId, revokedBySid, DateTimeOffset.UtcNow, reason, bindReqId: true, ct).ConfigureAwait(false) != 1)
        {
            return false;
        }

        await IncrementTableEpochInternalAsync(conn, tableId.Value, ct);

        await RecordAuditEventInternalAsync(conn, new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = "CONSENT_REVOKED",
            ActorSid = revokedBySid,
            TargetTable = tableId.Value.ToString(),
            Decision = ConsentGrantSqlCore.RevokedDecision,
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new
            {
                ConsentId = consentId,
                Reason = reason,
                SystemRevocation = true
            })
        }, ct);

        return true;
    }

    /// <summary>opt-in load of<c>CONSENTS.description</c> (<see cref="IConsentDescriptionRepository.LoadDescriptionsAsync"/>); never part of hydration.</summary>
    public async Task LoadDescriptionsAsync(IReadOnlyCollection<Consent> consents, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consents);
        if (consents.Count == 0) return;
        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        await ConsentSqlCore.LoadConsentDescriptionsAsync(scope.Connection, consents, ct).ConfigureAwait(false);
    }

    private Task HydrateConsentsBatchAsync(SqliteConnection conn, List<Consent> consents, CancellationToken ct) =>
        ConsentSqlCore.HydrateConsentsBatchAsync(conn, consents, ct);

    public async Task<(ConsentRowFilter Filter, ConsentEffect Effect, TenantId Tenant, TableIdentifier Table)?> GetRowFilterAsync(
        Guid rowFilterId, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct, snapshot: false).ConfigureAwait(false);
        var row = await RowFilterEvaluationStore.ReadRuleAsync(scope.Connection, null, CatalogKeyDefinitionStore.Sqlite, rowFilterId, ct).ConfigureAwait(false);
        return row is null ? null : (row.Filter, row.Effect, row.Tenant, row.Table);
    }

    public async Task<RowFilterEvaluationChange?> UpdateRowFilterEvaluationAsync(
        Guid rowFilterId, TenantId tenant, RowFilterEvaluationSettings settings, CancellationToken ct = default)
    {
        RowFilterEvaluationChange? change;
        await using (var scope = await WriteScopeAsync(ct).ConfigureAwait(false))
        {
            var conn = scope.Connection;
            using var tx = conn.BeginTransaction();
            try
            {
                change = await RowFilterEvaluationStore.UpdateRuleAsync(
                    conn, tx, CatalogKeyDefinitionStore.Sqlite, rowFilterId, tenant, settings,
                    table => BumpTableEpochAndReadAsync(conn, tx, table, ct), ct).ConfigureAwait(false);
                if (change is null)
                {
                    await tx.RollbackAsync(ct);
                    return null;
                }

                await tx.CommitAsync(ct);
                QueueMetadataInvalidation(change.TargetTable);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        await _epochValidationService.InvalidateEpochAsync(change.TargetTable, ct);
        return change;
    }

    public async Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        await ConsentSqlCore.CreateConsentRequestAsync(scope.Connection, findTableIdSql: null, request, ct).ConfigureAwait(false);
        return request;
    }

    public async Task<ConsentRequest?> GetConsentRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        return await ReadConsentRequestAsync(scope.Connection, null, requestId, ct).ConfigureAwait(false);
    }

    private static Task<ConsentRequest?> ReadConsentRequestAsync(SqliteConnection conn, SqliteTransaction? tx, Guid requestId, CancellationToken ct) =>
        ConsentSqlCore.ReadConsentRequestAsync(conn, tx, ConsentSql.SelectConsentRequestByIdSql, requestId, loadSteps: false, ct);

    public Task<ConsentRequest?> GetConsentRequestByTicketIdAsync(string ticketId, CancellationToken ct = default)
        => GetConsentRequestByTicketIdCoreAsync(ticketId, null, ct);

    public Task<ConsentRequest?> GetConsentRequestByTicketIdAsync(string ticketId, TenantId tenantId, CancellationToken ct = default)
        => GetConsentRequestByTicketIdCoreAsync(ticketId, tenantId, ct);

    private async Task<ConsentRequest?> GetConsentRequestByTicketIdCoreAsync(string ticketId, TenantId? tenantId, CancellationToken ct)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                                   r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                                   COALESCE(t.source_name, p.domain, 'default'), t.schema_name, t.table_name,
                                   r.itsm_ticket_id, r.tenant_id, r.requester_identifiers_json, r.context_json
                            FROM CONSENT_REQUESTS r
                            JOIN TABLES t ON r.table_id = t.id
                            LEFT JOIN POLICY_EPOCHS p ON t.id = p.table_id
                            WHERE r.itsm_ticket_id = @ticketId";
        cmd.Parameters.AddWithValue("@ticketId", ticketId);
        if (tenantId.HasValue)
        {
            cmd.CommandText += " AND r.tenant_id = @tenantId";
            cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
        }

        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ConsentSqlCore.ReadFullConsentRequestRow(reader) : null;
    }

    public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default)
        => ActivateConsentAsync(requestId, null, ct);

    public Task ActivateConsentAsync(Guid requestId, Sid? approvedBy, CancellationToken ct = default) =>
        ActivateConsentCoreAsync(requestId, approvedBy, allowPendingAutoApprove: false, ct);

    public Task ActivateConsentForAutoApproveAsync(Guid requestId, CancellationToken ct = default) =>
        ActivateConsentCoreAsync(requestId, null, allowPendingAutoApprove: true, ct);

    private ValueTask EnsureMayMaterializeAsync(TenantId tenant, TableIdentifier table, IReadOnlyList<ConsentRowFilter> filters, CancellationToken ct) =>
        _materializationGuard is null
            ? ValueTask.CompletedTask
            : _materializationGuard.EnsureMayMaterializeAsync(tenant, table, filters, ct);

    private async Task ActivateConsentCoreAsync(Guid requestId, Sid? approvedBy, bool allowPendingAutoApprove, CancellationToken ct)
    {
        var pending = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false);
        if (pending == null) return;
        await EnsureMayMaterializeAsync(pending.TenantId, pending.TableIdentifier, Array.Empty<ConsentRowFilter>(), ct).ConfigureAwait(false);

        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        var req = await ReadConsentRequestAsync(conn, null, requestId, ct).ConfigureAwait(false);
        if (req == null) return;

        using var tx = conn.BeginTransaction();
        var (activated, consentId) = await ConsentSqlCore.TryActivateConsentRowsAsync(conn, tx, req, allowPendingAutoApprove, "Allow", DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        if (!activated)
        {
            await tx.RollbackAsync(ct);
            return;
        }

        var auditEntry = new AuditLogEntry
        {
            TenantId = req.TenantId,
            EventType = "CONSENT_GRANTED",
            ActorSid = approvedBy ?? req.RequesterSid,
            TargetTable = req.TableIdentifier.ToString(),
            Decision = "APPROVED",
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new
            {
                ConsentId = consentId,
                RequestId = req.Id,
                GranteeType = req.RequestedGranteeType.ToString(),
                GranteeRef = req.RequestedGranteeRef,
                ValidTo = req.RequestedValidTo
            })
        };
        await RecordAuditEventInternalAsync(conn, auditEntry, ct, tx);

        await IncrementTableEpochInternalAsync(conn, req.TableIdentifier, tx, ct);
        await tx.CommitAsync(ct);
        OnTransactionCommitted(tx);

        await _epochValidationService.InvalidateEpochAsync(req.TableIdentifier, ct);
    }

    public async Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
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
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        await ConsentSqlCore.UpdateConsentRequestTicketIdAsync(scope.Connection, requestId, ticketId, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, string? status = null, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        return await ConsentSqlCore.GetRequestsByRequesterSidAsync(scope.Connection, ConsentSql.SelectRequestsByRequesterBaseSql, requesterSid, status, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        return await ConsentSqlCore.GetPendingRequestsForApproverAsync(scope.Connection, ConsentSql.SelectPendingRequestsForApproverSql, approverSid, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
    }

    public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default)
        => ApproveConsentRequestStepAsync(requestId, approverSid, isExternalItsmApproval: false, itsmApproverAccount: null, ct);

    public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, bool isExternalItsmApproval, CancellationToken ct = default)
        => ApproveConsentRequestStepAsync(requestId, approverSid, isExternalItsmApproval, itsmApproverAccount: null, ct);

    public async Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, bool isExternalItsmApproval, string? itsmApproverAccount, CancellationToken ct = default)
    {
        var req = await GetConsentRequestAsync(requestId, ct);
        if (req == null) throw new InvalidOperationException($"Request {requestId} not found.");

        ConsentApprovalPolicy.EnsureApprovableStatus(requestId, req.Status, isExternalItsmApproval);

        if (ConsentApprovalPolicy.IsSelfApproval(req, approverSid, itsmApproverAccount))
        {
            throw new InvalidOperationException("Separation of duties violated: The requester cannot approve their own request.");
        }

        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        string currentStatus = await ConsentSqlCore.ReadRequestStatusOrThrowAsync(conn, null, ConsentSql.SelectRequestStatusSql, requestId, ct).ConfigureAwait(false);
        ConsentApprovalPolicy.EnsureApprovableStatus(requestId, currentStatus, isExternalItsmApproval);

        var approverOwnerIds = await EnsureApproverIsNotRequesterAsync(conn, req, approverSid, itsmApproverAccount, ct);

        if (!isExternalItsmApproval)
        {
            if (!await IsAuthorizedApproverForTableInternalAsync(conn, req.TableIdentifier, approverSid, ct))
            {
                throw new UnauthorizedAccessException($"User '{approverSid}' is neither a data owner nor a delegated approver for table '{req.TableIdentifier}'.");
            }
        }
        else if (await ConsentSqlCore.HasConfiguredDataOwnersAsync(conn, ConsentSql.CountConfiguredOwnersSql, req.TableIdentifier, ct).ConfigureAwait(false))
        {
            string? account = string.IsNullOrWhiteSpace(itsmApproverAccount) ? null : itsmApproverAccount.Trim();
            if (account == null)
            {
                throw new UnauthorizedAccessException($"ITSM approval without a named approver is not allowed for table '{req.TableIdentifier}' with configured data owners.");
            }

            if (!await IsAuthorizedApproverForTableInternalAsync(conn, req.TableIdentifier, approverSid, account, ct))
            {
                throw new UnauthorizedAccessException($"ITSM approver '{account}' is neither a data owner nor a delegated approver for table '{req.TableIdentifier}'.");
            }
        }

        var state = await ReadApprovalStateAsync(conn, req, approverSid, itsmApproverAccount, approverOwnerIds, ct);
        if (state.SameApprover)
        {
            if (isExternalItsmApproval)
            {
                return req;
            }

            throw new InvalidOperationException(ConsentApprovalPolicy.SameApproverViolationMessage);
        }

        int nextStep = state.NextStepNumber;
        string newStatus = ConsentApprovalPolicy.StatusAfterApproval(state.RequiresFourEyes, nextStep, isExternalItsmApproval, state.Sensitivity);

        using var tx = conn.BeginTransaction();
        await ConsentSqlCore.InsertApprovedStepAsync(conn, tx, Guid.NewGuid(), requestId, nextStep, approverSid, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        await ApplyGuardedStatusAsync(conn, tx, requestId, newStatus, currentStatus, ct);

        req.Status = newStatus;
        await RecordAuditEventInternalAsync(conn,
            ConsentApprovalPolicy.BuildStepAudit(req, approverSid, "CONSENT_APPROVAL_STEP", "APPROVED", newStatus, itsmApproverAccount, null), ct, tx);

        tx.Commit();
        OnTransactionCommitted(tx);
        return req;
    }

    private async Task ApplyGuardedStatusAsync(SqliteConnection conn, SqliteTransaction tx, Guid requestId, string newStatus, string expectedStatus, CancellationToken ct)
    {
        try
        {
            await ConsentSqlCore.ApplyGuardedStatusAsync(conn, tx, ConsentStatusUpdateSeam, requestId, newStatus, expectedStatus, ct).ConfigureAwait(false);
        }
        catch (ConsentRequestStatusConflictException)
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            OnTransactionRolledBack(tx);
            throw;
        }
    }

    public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) =>
        RejectConsentRequestAsync(requestId, approverSid, reason, isExternalItsm: false, ct);

    public async Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, bool isExternalItsm, CancellationToken ct = default)
    {
        var req = await GetConsentRequestAsync(requestId, ct);
        if (req == null) throw new InvalidOperationException($"Request {requestId} not found.");

        if (!ConsentApprovalPolicy.IsRejectableStatus(req.Status))
        {
            throw new ConsentRequestStatusConflictException(requestId);
        }

        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        string currentStatus = await ConsentSqlCore.ReadRequestStatusOrThrowAsync(conn, null, ConsentSql.SelectRequestStatusSql, requestId, ct).ConfigureAwait(false);
        if (!ConsentApprovalPolicy.IsRejectableStatus(currentStatus))
        {
            throw new ConsentRequestStatusConflictException(requestId);
        }

        bool isAuthorized = isExternalItsm ||
                            await IsAuthorizedApproverForTableInternalAsync(conn, req.TableIdentifier, approverSid, ct);
        if (!isAuthorized)
        {
            throw new UnauthorizedAccessException($"User '{approverSid}' is neither a data owner nor a delegated approver for table '{req.TableIdentifier}'.");
        }

        using var tx = conn.BeginTransaction();
        await ConsentSqlCore.InsertRejectedStepAsync(conn, tx, requestId, approverSid, reason, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        await ApplyGuardedStatusAsync(conn, tx, requestId, "REJECTED", currentStatus, ct);

        req.Status = "REJECTED";
        await RecordAuditEventInternalAsync(conn,
            ConsentApprovalPolicy.BuildStepAudit(req, approverSid, "CONSENT_REQUEST_REJECTED", "REJECTED", "REJECTED", null, reason), ct, tx);

        tx.Commit();
        OnTransactionCommitted(tx);
        return req;
    }

    public async Task<bool> WithdrawConsentRequestAsync(Guid requestId, Sid requesterSid, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        using var tx = conn.BeginTransaction();
        try
        {
            if (!await ConsentSqlCore.ExecuteWithdrawUpdateAsync(conn, tx, requestId, requesterSid, ct).ConfigureAwait(false))
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            var req = await ReadConsentRequestAsync(conn, tx, requestId, ct).ConfigureAwait(false);
            if (req == null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            req.Status = "WITHDRAWN";
            await RecordAuditEventInternalAsync(conn,
                ConsentApprovalPolicy.BuildStepAudit(req, requesterSid, AuditEventTypes.ConsentWithdrawn, "WITHDRAWN", "WITHDRAWN", null, "Access request withdrawn by requester."),
                ct, tx).ConfigureAwait(false);

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

        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;

        string currentStatus = await ConsentSqlCore.ReadRequestStatusOrThrowAsync(conn, null, ConsentSql.SelectRequestStatusSql, requestId, ct).ConfigureAwait(false);
        ConsentApprovalPolicy.EnsureApprovableStatus(requestId, currentStatus, isExternalItsmApproval: false);

        var approverOwnerIds = await EnsureApproverIsNotRequesterAsync(conn, req, step.ApproverSid, null, ct).ConfigureAwait(false);
        var state = await ReadApprovalStateAsync(conn, req, step.ApproverSid, null, approverOwnerIds, ct).ConfigureAwait(false);
        if (state.SameApprover)
        {
            throw new InvalidOperationException(ConsentApprovalPolicy.SameApproverViolationMessage);
        }

        string newStatus = ConsentApprovalPolicy.StatusAfterApproval(state.RequiresFourEyes, state.NextStepNumber, isExternalItsmApproval: false, state.Sensitivity);
        if (!string.Equals(newStatus, ConsentApprovalPolicy.Approved, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(ConsentApprovalPolicy.FourEyesNotSatisfiedMessage(requestId));
        }

        using var tx = conn.BeginTransaction();
        try
        {
            await ConsentSqlCore.InsertApprovedStepAsync(conn, tx, step.Id, requestId, state.NextStepNumber, step.ApproverSid, step.DecidedAt ?? DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            await ApplyGuardedStatusAsync(conn, tx, requestId, newStatus, currentStatus, ct).ConfigureAwait(false);

            req.Status = newStatus;
            await RecordAuditEventInternalAsync(conn,
                ConsentApprovalPolicy.BuildStepAudit(req, step.ApproverSid, "CONSENT_APPROVAL_STEP", "APPROVED", newStatus, null, null), ct, tx).ConfigureAwait(false);

            await InsertConsentCoreAsync(conn, tx, consent, ct).ConfigureAwait(false);
            await IncrementTableEpochInternalAsync(conn, consent.TableIdentifier, tx, ct).ConfigureAwait(false);

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

    private async Task<IReadOnlyCollection<string>> EnsureApproverIsNotRequesterAsync(SqliteConnection conn, ConsentRequest req, Sid approverSid, string? itsmApproverAccount, CancellationToken ct)
    {
        if (ConsentApprovalPolicy.IsSelfApproval(req, approverSid, itsmApproverAccount))
        {
            throw new InvalidOperationException("Separation of duties violated: The requester cannot approve their own consent request.");
        }

        var approverOwnerIds = await ResolveDataOwnerIdsInternalAsync(conn, ConsentApprovalPolicy.IdentifierCandidates(approverSid.Value, itsmApproverAccount), ct);
        if (ConsentApprovalPolicy.ShareOwnerId(approverOwnerIds, await ResolveRequesterOwnerIdsInternalAsync(conn, req, ct)))
        {
            throw new InvalidOperationException("Separation of duties violated: The requester cannot approve their own consent request.");
        }

        return approverOwnerIds;
    }

    private Task<(bool RequiresFourEyes, string? Sensitivity, bool SameApprover, int NextStepNumber)> ReadApprovalStateAsync(
        SqliteConnection conn,
        ConsentRequest req,
        Sid approverSid,
        string? itsmApproverAccount,
        IReadOnlyCollection<string> approverOwnerIds,
        CancellationToken ct) =>
        ConsentSqlCore.ReadApprovalStateAsync(
            conn,
            null,
            req.Id,
            approverSid,
            itsmApproverAccount,
            approverOwnerIds,
            (candidates, token) => ResolveDataOwnerIdsInternalAsync(conn, candidates, token),
            ct);

    public async Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default)
    {
        StoredTenant.RequireWritable(consent.TenantId);
        consent.Validate();
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        using var tx = conn.BeginTransaction();
        await InsertConsentCoreAsync(conn, tx, consent, ct);

        await IncrementTableEpochInternalAsync(conn, consent.TableIdentifier, tx, ct);
        await tx.CommitAsync(ct);
        OnTransactionCommitted(tx);

        await _epochValidationService.InvalidateEpochAsync(consent.TableIdentifier, ct);
        return consent;
    }

    /// <summary>Inserts the consent row, its column rules and row filters and the grant audit entry inside <paramref name="tx"/>. Neither bumps the epoch nor commits.</summary>
    private async Task InsertConsentCoreAsync(SqliteConnection conn, SqliteTransaction tx, Consent consent, CancellationToken ct)
    {
        const string insertConsentSql = @"INSERT INTO CONSENTS (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, grantee_sid_norm, role_id, role_name, valid_from, valid_to, is_revoked, tenant_id, description)
                                VALUES (@id, @tid, @reqId, @effect, @granteeType, @granteeSid, @sidNorm, @roleId, @roleName, @validFrom, @validTo, 0, @tenantId, @description)";

        var (_, auditEntry) = await ConsentSqlCore.InsertConsentRowsAsync(conn, tx, insertConsentSql, findTableIdSql: null, consent, ct).ConfigureAwait(false);
        await RecordAuditEventInternalAsync(conn, auditEntry, ct, tx);
    }

    public async Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        var conn = scope.Connection;
        var consent = await ConsentSqlCore.ReadConsentByIdRowAsync(conn, consentId, ct).ConfigureAwait(false);
        if (consent != null)
        {
            await HydrateConsentsBatchAsync(conn, [consent], ct).ConfigureAwait(false);
        }

        return consent;
    }

    public async Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default)
    {
        await using var scope = await ReadScopeAsync(ct).ConfigureAwait(false);
        var consents = await ConsentSqlCore.ReadExpiringConsentRowsAsync(
            scope.Connection, ConsentSql.SelectExpiringConsentsSql, DateTimeOffset.UtcNow, threshold, maxResults, ct).ConfigureAwait(false);
        await HydrateConsentsBatchAsync(scope.Connection, consents, ct).ConfigureAwait(false);
        return consents;
    }

    public async Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default)
    {
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        var (tableId, extendTenant, _) = await ConsentSqlCore.ReadAndExtendConsentExpiryAsync(
            conn, null, ConsentSql.SelectExtendTargetSql, consentId, newValidTo, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

        if (tableId.HasValue)
        {
            await IncrementTableEpochInternalAsync(conn, tableId.Value, ct);
        }

        await RecordAuditEventInternalAsync(conn, ConsentSqlCore.BuildExpiryExtendedAudit(extendTenant, tableId, consentId, newValidTo), ct);
    }
}
