namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

public partial class GovernanceRepository
{
    private const int ConsentHydrateBatchSize = 500;

    internal static class ConsentSql
    {
        public const string FindTableIdSql =
            "SELECT id FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)";

        public const string SelectExpiringConsentsSql = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                   c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                   c.is_revoked, t.source_name, t.schema_name, t.table_name,
                                   c.tenant_id, c.renewal_count
                            FROM CONSENTS c
                            JOIN TABLES t ON c.table_id = t.id
                            WHERE c.is_revoked = 0 AND c.valid_to > @now AND c.valid_to <= @threshold
                            ORDER BY c.valid_to
                            OFFSET 0 ROWS FETCH NEXT @maxResults ROWS ONLY";

        public const string SelectConsentRequestByIdSql = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                                       r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                                       r.itsm_ticket_id, t.source_name, t.schema_name, t.table_name,
                                       r.tenant_id, r.requester_identifiers_json, r.context_json
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
                                       r.itsm_ticket_id, t.source_name, t.schema_name, t.table_name,
                                       r.tenant_id, r.requester_identifiers_json, r.context_json
                                FROM CONSENT_REQUESTS r
                                JOIN TABLES t ON r.table_id = t.id
                                WHERE r.status IN ('PENDING', 'PENDING_SECOND_APPROVAL')
                                  AND (
                                      EXISTS (
                                          SELECT 1
                                          FROM TABLE_OWNERS tow
                                          JOIN DATA_OWNERS dow ON tow.data_owner_id = dow.id
                                          WHERE tow.table_id = r.table_id
                                            AND (dow.ad_sid = @apprSid OR (@itsmAccount IS NOT NULL AND (LOWER(dow.ad_account) = LOWER(@itsmAccount) OR LOWER(dow.email) = LOWER(@itsmAccount))))
                                      )
                                      OR EXISTS (
                                          SELECT 1
                                          FROM DATA_OWNER_DELEGATIONS del
                                          JOIN TABLE_OWNERS tow ON del.data_owner_id = tow.data_owner_id
                                          WHERE tow.table_id = r.table_id
                                            AND (del.delegate_sid = @apprSid OR (@itsmAccount IS NOT NULL AND LOWER(del.delegate_sid) = LOWER(@itsmAccount)))
                                            AND del.valid_from <= @now AND @now < del.valid_to
                                      )
                                      OR EXISTS (
                                          SELECT 1
                                          FROM ROLE_MEMBERS rm
                                          JOIN ROLES rol ON rm.role_id = rol.id
                                          WHERE (rol.role_name = 'GovernanceAdmin' OR rol.role_name = 'ClusterAdmin')
                                            AND (rm.member_sid = @apprSid OR (@itsmAccount IS NOT NULL AND LOWER(rm.member_sid) = LOWER(@itsmAccount)))
                                      )
                                  )
                                ORDER BY r.requested_at ASC";

        public const string CountConfiguredOwnersSql = @"SELECT COUNT(*)
                            FROM TABLES t
                            JOIN TABLE_OWNERS tow ON t.id = tow.table_id
                            WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table)";

        public const string SelectRequestIdByTicketSql =
            "SELECT id FROM CONSENT_REQUESTS WHERE itsm_ticket_id = @ticket ORDER BY requested_at, id OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY";

        public const string SelectRequestIdByTicketAndTenantSql =
            "SELECT id FROM CONSENT_REQUESTS WHERE itsm_ticket_id = @ticket AND tenant_id = @tenant ORDER BY requested_at, id OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY";

        public const string UpdateSystemRevokeSql = @"UPDATE CONSENTS
                                          SET is_revoked = 1, revoked_by_sid = @sid, revoked_at = @now, revoke_reason = @reason
                                          WHERE id = @id AND is_revoked = 0";
    }

    // Transitional: StartsWith("WITH") engine sniffing goes away when lock clauses live in the dialect.
    private string BuildLockRowsQuery(string selectColumns, string fromTableAndAlias, string joinsAndWhere, string? lockTargetAlias = null)
    {
        if (_dialect.LockRowsForUpdate.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
        {
            return "SELECT " + selectColumns + " FROM " + fromTableAndAlias + " " + _dialect.LockRowsForUpdate + " " + joinsAndWhere;
        }

        var lockClause = lockTargetAlias != null ? "FOR UPDATE OF " + lockTargetAlias : "FOR UPDATE";
        return "SELECT " + selectColumns + " FROM " + fromTableAndAlias + " " + joinsAndWhere + " " + lockClause;
    }

    private string SelectExtendTargetSql =>
        BuildLockRowsQuery("t.source_name, t.schema_name, t.table_name, c.tenant_id, c.valid_from", "CONSENTS c", "JOIN TABLES t ON c.table_id = t.id WHERE c.id = @id", "c");

    // Transitional: StartsWith("WITH") engine sniffing goes away when lock clauses live in the dialect.
    private string SelectRequestStatusForUpdateSql =>
        _dialect.LockRowsForUpdate.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
            ? "SELECT status FROM CONSENT_REQUESTS WITH (UPDLOCK, ROWLOCK, HOLDLOCK) WHERE id = @id"
            : "SELECT status FROM CONSENT_REQUESTS WHERE id = @id FOR UPDATE";

    private string SelectSystemRevokeTargetSql =>
        BuildLockRowsQuery("t.source_name, t.schema_name, t.table_name, c.tenant_id", "CONSENTS c", "JOIN TABLES t ON c.table_id = t.id WHERE c.id = @id AND c.consent_request_id = @reqId AND c.is_revoked = 0", null);

    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(
        IEnumerable<Sid> subjects,
        TableIdentifier table,
        DateTimeOffset atTime,
        CancellationToken ct = default) =>
        // Explicit cross-tenant read path of the tenant-less interface overload; the tenant overload below fails closed.
        ActiveForSubjectsCoreAsync(subjects, table, atTime, null, ct);

    public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(
        IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) =>
        ActiveForSubjectsCoreAsync(subjects, table, atTime, TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.Consents)), ct);

    private async Task<IReadOnlyList<Consent>> ActiveForSubjectsCoreAsync(
        IEnumerable<Sid> subjects,
        TableIdentifier table,
        DateTimeOffset atTime,
        TenantId? tenantId,
        CancellationToken ct)
    {
        var subjectEnumerated = subjects.ToList();
        var subjectSet = subjectEnumerated.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (subjectSet.Count == 0) return Array.Empty<Consent>();
        var normalized = ConsentSubjectFilter.DistinctNormalized(subjectEnumerated);

        var consents = new List<Consent>();
        var seen = new HashSet<Guid>();
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        foreach (var chunk in ConsentSubjectFilter.Chunks(normalized))
        {
            await using var cmd = conn.CreateCommand();
            var tenantFilter = tenantId != null ? " AND c.tenant_id = @tenantId" : "";
            cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                       c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                       c.is_revoked, c.revoked_by_sid, c.revoked_at, c.revoke_reason,
                                       c.tenant_id
                                FROM CONSENTS c
                                JOIN TABLES t ON c.table_id = t.id
                                WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table)
                                  AND c.is_revoked = 0" + tenantFilter + ConsentSubjectFilter.InListPredicate(chunk.Count);
            for (var si = 0; si < chunk.Count; si++)
            {
                cmd.AddBound("@s" + si, chunk[si]);
            }

            cmd.AddBound("@domain", table.Domain);
            cmd.AddBound("@schema", table.Schema);
            cmd.AddBound("@table", table.TableName);
            if (tenantId != null)
            {
                cmd.AddBound("@tenantId", tenantId.Value.Value);
            }

            await using var reader = await ExecuteReaderAsync(cmd, "Consent.GetActiveForSubjects", ct).ConfigureAwait(false);
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
        // Explicit cross-tenant read path of the tenant-less interface overload; the tenant overload below fails closed.
        AllActiveCoreAsync(subjects, roles, atTime, null, ct);

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(
        IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) =>
        AllActiveCoreAsync(subjects, roles, atTime, TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.Consents)), ct);

    private async Task<IReadOnlyList<Consent>> AllActiveCoreAsync(
        IEnumerable<Sid> subjects,
        IEnumerable<string>? roles,
        DateTimeOffset? atTime,
        TenantId? tenantId,
        CancellationToken ct)
    {
        var effectiveAt = atTime ?? TimeProvider.System.GetUtcNow();
        var subjectEnumerated = subjects.ToList();
        var subjectSet = subjectEnumerated.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roleSet = roles?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (subjectSet.Count == 0 && roleSet.Count == 0) return Array.Empty<Consent>();

        var normalized = ConsentSubjectFilter.DistinctNormalized(subjectEnumerated);
        var consents = new List<Consent>();
        var seen = new HashSet<Guid>();
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        foreach (var chunk in ConsentSubjectFilter.Chunks(normalized))
        {
            await using var cmd = conn.CreateCommand();
            var tenantFilter = tenantId != null ? " AND c.tenant_id = @tenantId" : "";
            cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                       c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                       c.is_revoked, t.source_name, t.schema_name, t.table_name,
                                       c.tenant_id
                                FROM CONSENTS c
                                JOIN TABLES t ON c.table_id = t.id
                                WHERE c.is_revoked = 0" + tenantFilter + ConsentSubjectFilter.InListPredicate(chunk.Count);
            for (var si = 0; si < chunk.Count; si++)
            {
                cmd.AddBound("@s" + si, chunk[si]);
            }

            if (tenantId != null)
            {
                cmd.AddBound("@tenantId", tenantId.Value.Value);
            }

            await using var reader = await ExecuteReaderAsync(cmd, "Consent.GetAllActiveForSubjects", ct).ConfigureAwait(false);
            ConsentSqlCore.ReadAllSubjectConsentRows(reader, effectiveAt, subjectSet, roleSet, seen, consents);
        }

        consents.RemoveAll(c => !c.IsActive(effectiveAt) && !(c.Effect == ConsentEffect.Deny && c.ValidFrom > effectiveAt));
        await HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        return consents;
    }

    public async Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(
        Guid consentRequestId,
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var consents = await ConsentSqlCore.ReadActiveConsentsByRequestIdAsync(conn, consentRequestId, atTime, false, ct).ConfigureAwait(false);
        await HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        return consents;
    }

    public async Task LoadDescriptionsAsync(IReadOnlyCollection<Consent> consents, CancellationToken ct = default)
    {
        if (consents.Count == 0) return;
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await ConsentSqlCore.LoadDescriptionsBatchAsync(conn, consents, ct).ConfigureAwait(false);
    }

    public async Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consent);
        StoredTenant.RequireWritable(consent.TenantId);
        TenantScope.RequireTenant(consent.TenantId, nameof(GovernanceTable.Consents));
        consent.Validate();

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var created = await InsertConsentCoreAsync(conn, tx, consent, ct).ConfigureAwait(false);
            await IncrementTableEpochInternalAsync(conn, tx, consent.TableIdentifier, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            await InvalidateTableEpochAsync(consent.TableIdentifier, ct).ConfigureAwait(false);
            return created;
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Consent> InsertConsentCoreAsync(DbConnection conn, DbTransaction tx, Consent consent, CancellationToken ct)
    {
        const string insertConsentSql = @"INSERT INTO CONSENTS (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, grantee_sid_norm, role_id, role_name, valid_from, valid_to, is_revoked, tenant_id, description)
                                VALUES (@id, @tid, @reqId, @effect, @granteeType, @granteeSid, @sidNorm, @roleId, @roleName, @validFrom, @validTo, 0, @tenantId, @description)";
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = insertConsentSql;
        cmd.AddBound("@id", consent.Id.ToString());
        cmd.AddBound("@tid", consent.TableId.ToString());
        cmd.AddBound("@reqId", consent.ConsentRequestId.HasValue ? consent.ConsentRequestId.Value.ToString() : (object)DBNull.Value);
        cmd.AddBound("@effect", consent.Effect.ToString());
        cmd.AddBound("@granteeType", consent.GranteeType.ToString());
        cmd.AddBound("@granteeSid", consent.GranteeSid.HasValue ? consent.GranteeSid.Value.Value : (object)DBNull.Value);
        cmd.AddBound("@sidNorm", ConsentSubjectFilter.NormalizeParameter(consent.GranteeSid?.Value));
        cmd.AddBound("@roleId", consent.RoleId.HasValue ? consent.RoleId.Value.ToString() : (object)DBNull.Value);
        cmd.AddBound("@roleName", (object?)consent.RoleName ?? DBNull.Value);
        cmd.AddBound("@validFrom", consent.ValidFrom.ToString("O"));
        cmd.AddBound("@validTo", consent.ValidTo.ToString("O"));
        cmd.AddBound("@tenantId", consent.TenantId.Value);
        cmd.AddBound("@description", (object?)consent.Description ?? DBNull.Value);
        await ExecuteNonQueryAsync(cmd, "Consent.InsertConsent", ct).ConfigureAwait(false);

        await ConsentSqlCore.InsertColumnRulesAndRowFiltersAsync(conn, tx, consent, ct).ConfigureAwait(false);

        var auditEntry = new AuditLogEntry
        {
            TenantId = consent.TenantId,
            EventType = AuditEventTypes.ConsentGranted,
            ActorSid = consent.GranteeSid ?? new Sid(consent.RoleName ?? "UNKNOWN"),
            TargetTable = consent.TableIdentifier.ToString(),
            Decision = "ALLOW",
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new
            {
                ConsentId = consent.Id,
                consent.Effect,
                Grantee = consent.GranteeSid?.Value ?? consent.RoleName,
                Scope = consent.TableIdentifier.ToString(),
                consent.ValidFrom,
                consent.ValidTo
            })
        };
        await RecordAuditEventAsync(auditEntry, tx, ct).ConfigureAwait(false);
        return consent;
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
                conn, tx, SelectSystemRevokeTargetSql, consentId, consentRequestId, ct).ConfigureAwait(false);
            if (foundTable is null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            tableId = foundTable;
            var tenant = ConsentGrantSqlCore.ResolveConsentTenant(rawTenant, consentId, tableId.Value.ToString());
            TenantScope.RequireTenant(tenant, nameof(GovernanceTable.Consents));
            StoredTenant.RequireWritable(tenant);

            var now = TimeProvider.System.GetUtcNow();
            await ConsentSqlCore.ExecuteSystemRevokeUpdateAsync(
                conn, tx, ConsentSql.UpdateSystemRevokeSql, consentId, consentRequestId, revokedBySid, now, reason, bindReqId: false, ct).ConfigureAwait(false);

            var entry = new AuditLogEntry
            {
                TenantId = tenant,
                EventType = AuditEventTypes.ConsentRevoked,
                ActorSid = revokedBySid,
                TargetTable = tableId.Value.ToString(),
                Decision = "REVOKED",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    ConsentId = consentId,
                    RevokedBy = revokedBySid.Value,
                    Reason = reason,
                    ConsentRequestId = consentRequestId,
                    Scope = tableId.Value.ToString(),
                    RevokedAt = now
                })
            };
            await RecordAuditEventAsync(entry, tx, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }

        if (tableId.HasValue)
        {
            await InvalidateTableEpochAsync(tableId.Value, ct).ConfigureAwait(false);
        }

        return true;
    }

    public async Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ConsentSqlCore.ReadExpiringConsentRowsAsync(conn, ConsentSql.SelectExpiringConsentsSql, TimeProvider.System.GetUtcNow(), threshold, maxResults, ct).ConfigureAwait(false);
    }

    public async Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default)
    {
        TableIdentifier? tableId;
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            TenantId extendTenant;
            (tableId, extendTenant, _) = await ConsentSqlCore.ReadAndExtendConsentExpiryAsync(
                conn, tx, SelectExtendTargetSql, consentId, newValidTo, TimeProvider.System.GetUtcNow(), ct).ConfigureAwait(false);

            TenantScope.RequireTenant(extendTenant, nameof(GovernanceTable.Consents));
            StoredTenant.RequireWritable(extendTenant);

            await RecordAuditEventAsync(ConsentSqlCore.BuildExpiryExtendedAudit(extendTenant, tableId, consentId, newValidTo), tx, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }

        if (tableId.HasValue)
        {
            await InvalidateTableEpochAsync(tableId.Value, ct).ConfigureAwait(false);
        }
    }

    public async Task<(ConsentRowFilter Filter, ConsentEffect Effect, TenantId Tenant, TableIdentifier Table)?> GetRowFilterAsync(
        Guid rowFilterId, CancellationToken ct = default)
    {
        // Transitional: engine-type branch; replace with a dialect member.
        var keyDialect = _dialect is PostgreSqlGovernanceDialect ? CatalogKeyDefinitionStore.PostgreSql : CatalogKeyDefinitionStore.SqlServer;
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var row = await RowFilterEvaluationStore.ReadRuleAsync(conn, null, keyDialect, rowFilterId, ct).ConfigureAwait(false);
        return row is null ? null : (row.Filter, row.Effect, row.Tenant, row.Table);
    }

    public async Task<RowFilterEvaluationChange?> UpdateRowFilterEvaluationAsync(
        Guid rowFilterId, TenantId tenant, RowFilterEvaluationSettings settings, CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenant, nameof(GovernanceTable.ConsentRowFilters));
        // Transitional: engine-type branch; replace with a dialect member.
        var keyDialect = _dialect is PostgreSqlGovernanceDialect ? CatalogKeyDefinitionStore.PostgreSql : CatalogKeyDefinitionStore.SqlServer;
        RowFilterEvaluationChange? change;
        await using (var conn = await OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                change = await RowFilterEvaluationStore.UpdateRuleAsync(
                    conn, tx, keyDialect, rowFilterId, tenant, settings,
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

        await InvalidateTableEpochAsync(change.TargetTable, ct).ConfigureAwait(false);
        return change;
    }

    public async Task<ConsentRequest> CreateConsentRequestAsync(ConsentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        TenantScope.RequireTenant(request.TenantId, nameof(GovernanceTable.ConsentRequests));
        StoredTenant.RequireWritable(request.TenantId);

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
        return await ConsentSqlCore.GetPendingRequestsForApproverAsync(conn, ConsentSql.SelectPendingRequestsForApproverSql, approverSid, TimeProvider.System.GetUtcNow(), ct).ConfigureAwait(false);
    }

    public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default)
        => ApproveConsentRequestStepAsync(requestId, approverSid, isExternalItsmApproval: false, itsmApproverAccount: null, ct);

    public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, bool isExternalItsmApproval, CancellationToken ct = default)
        => ApproveConsentRequestStepAsync(requestId, approverSid, isExternalItsmApproval, itsmApproverAccount: null, ct);

    public async Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, bool isExternalItsmApproval, string? itsmApproverAccount, CancellationToken ct = default)
    {
        var req = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Request {requestId} not found.");

        TenantScope.RequireTenant(req.TenantId, nameof(GovernanceTable.ConsentRequests));
        StoredTenant.RequireWritable(req.TenantId);

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
            string currentStatus = await ConsentSqlCore.ReadRequestStatusOrThrowAsync(conn, tx, SelectRequestStatusForUpdateSql, requestId, ct).ConfigureAwait(false);
            ConsentApprovalPolicy.EnsureApprovableStatus(requestId, currentStatus, isExternalItsmApproval);

            var state = await ConsentSqlCore.ReadApprovalStateAsync(
                conn, tx, requestId, approverSid, itsmApproverAccount, approverOwnerIds,
                ids => ResolveDataOwnerIdsInternalAsync(ids, ct), ct).ConfigureAwait(false);

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
            await ConsentSqlCore.InsertApprovedStepAsync(conn, tx, Guid.NewGuid(), requestId, stepNumber, approverSid, TimeProvider.System.GetUtcNow(), ct).ConfigureAwait(false);

            string newStatus = ConsentApprovalPolicy.StatusAfterApproval(state.RequiresFourEyes, stepNumber, isExternalItsmApproval, state.Sensitivity);
            await ConsentSqlCore.ApplyGuardedStatusAsync(conn, tx, ConsentStatusUpdateSeam, requestId, newStatus, currentStatus, ct).ConfigureAwait(false);

            req.Status = newStatus;
            var auditEntry = ConsentApprovalPolicy.BuildStepAudit(req, approverSid, "CONSENT_APPROVAL_STEP", "APPROVED", newStatus, itsmApproverAccount, null);
            await RecordAuditEventAsync(auditEntry, tx, ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return req;
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
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
            return true;
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public Task<Consent> ExecuteAtomicApprovalWithConsentAsync(
        Guid requestId,
        Sid approverSid,
        Consent consent,
        CancellationToken ct = default)
    {
        var step = new ApprovalStep
        {
            Id = Guid.NewGuid(),
            ConsentRequestId = requestId,
            StepNumber = 1,
            ApproverSid = approverSid,
            Decision = "APPROVED",
            DecidedAt = TimeProvider.System.GetUtcNow()
        };
        return ExecuteAtomicApprovalWithConsentAsync(requestId, step, consent, consent?.RowFilters ?? Array.Empty<ConsentRowFilter>(), consent?.ColumnRules ?? Array.Empty<ConsentColumnRule>(), ct);
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
        StoredTenant.RequireWritable(consent.TenantId);
        TenantScope.RequireTenant(consent.TenantId, nameof(GovernanceTable.Consents));

        consent.RowFilters = rowFilters ?? Array.Empty<ConsentRowFilter>();
        consent.ColumnRules = columnRules ?? Array.Empty<ConsentColumnRule>();
        consent.Validate();
        await EnsureMayMaterializeAsync(consent.TenantId, consent.TableIdentifier, consent.RowFilters, ct).ConfigureAwait(false);

        var req = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Request {requestId} not found.");

        TenantScope.RequireTenant(req.TenantId, nameof(GovernanceTable.ConsentRequests));
        StoredTenant.RequireWritable(req.TenantId);

        var approverOwnerIds = await EnsureApproverIsNotRequesterAsync(req, step.ApproverSid, null, ct).ConfigureAwait(false);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            string currentStatus = await ConsentSqlCore.ReadRequestStatusOrThrowAsync(conn, tx, SelectRequestStatusForUpdateSql, requestId, ct).ConfigureAwait(false);
            ConsentApprovalPolicy.EnsureApprovableStatus(requestId, currentStatus, isExternalItsmApproval: false);

            var state = await ConsentSqlCore.ReadApprovalStateAsync(
                conn, tx, requestId, step.ApproverSid, null, approverOwnerIds,
                ids => ResolveDataOwnerIdsInternalAsync(ids, ct), ct).ConfigureAwait(false);

            if (state.SameApprover)
            {
                throw new InvalidOperationException(ConsentApprovalPolicy.SameApproverViolationMessage);
            }

            string newStatus = ConsentApprovalPolicy.StatusAfterApproval(state.RequiresFourEyes, state.NextStepNumber, isExternalItsmApproval: false, state.Sensitivity);
            if (!string.Equals(newStatus, ConsentApprovalPolicy.Approved, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(ConsentApprovalPolicy.FourEyesNotSatisfiedMessage(requestId));
            }

            await ConsentSqlCore.InsertApprovedStepAsync(conn, tx, step.Id, requestId, state.NextStepNumber, step.ApproverSid, step.DecidedAt ?? TimeProvider.System.GetUtcNow(), ct).ConfigureAwait(false);
            await ConsentSqlCore.ApplyGuardedStatusAsync(conn, tx, ConsentStatusUpdateSeam, requestId, newStatus, currentStatus, ct).ConfigureAwait(false);

            req.Status = newStatus;
            await RecordAuditEventAsync(
                ConsentApprovalPolicy.BuildStepAudit(req, step.ApproverSid, "CONSENT_APPROVAL_STEP", "APPROVED", newStatus, null, null),
                tx, ct).ConfigureAwait(false);

            var createdConsent = await InsertConsentCoreAsync(conn, tx, consent, ct).ConfigureAwait(false);
            await IncrementTableEpochInternalAsync(conn, tx, consent.TableIdentifier, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            await InvalidateTableEpochAsync(consent.TableIdentifier, ct).ConfigureAwait(false);
            return createdConsent;
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    private ValueTask EnsureMayMaterializeAsync(TenantId tenant, TableIdentifier table, IReadOnlyList<ConsentRowFilter> filters, CancellationToken ct) =>
        _materializationGuard is null
            ? ValueTask.CompletedTask
            : _materializationGuard.EnsureMayMaterializeAsync(tenant, table, filters, ct);

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

    private async Task<bool> HasConfiguredDataOwnersAsync(TableIdentifier table, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ConsentSqlCore.HasConfiguredDataOwnersAsync(conn, ConsentSql.CountConfiguredOwnersSql, table, ct).ConfigureAwait(false);
    }

    public Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default) =>
        RejectConsentRequestAsync(requestId, approverSid, reason, isExternalItsm: false, ct);

    public async Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, bool isExternalItsm, CancellationToken ct = default)
    {
        var req = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Request {requestId} not found.");

        TenantScope.RequireTenant(req.TenantId, nameof(GovernanceTable.ConsentRequests));
        StoredTenant.RequireWritable(req.TenantId);

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
            string currentStatus = await ConsentSqlCore.ReadRequestStatusOrThrowAsync(conn, tx, SelectRequestStatusForUpdateSql, requestId, ct).ConfigureAwait(false);
            if (!ConsentApprovalPolicy.IsRejectableStatus(currentStatus))
            {
                throw new Autheris.Application.Governance.ConsentRequests.ConsentRequestStatusConflictException(requestId);
            }

            await ConsentSqlCore.InsertRejectedStepAsync(conn, tx, requestId, approverSid, reason, TimeProvider.System.GetUtcNow(), ct).ConfigureAwait(false);
            await ConsentSqlCore.ApplyGuardedStatusAsync(conn, tx, ConsentStatusUpdateSeam, requestId, "REJECTED", currentStatus, ct).ConfigureAwait(false);

            req.Status = "REJECTED";
            var auditEntry = ConsentApprovalPolicy.BuildStepAudit(req, approverSid, "CONSENT_REQUEST_REJECTED", "REJECTED", "REJECTED", null, reason);
            await RecordAuditEventAsync(auditEntry, tx, ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return req;
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ConsentRequest?> GetConsentRequestByTicketIdAsync(string ticketId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var id = await ConsentSqlCore.FindRequestIdByTicketIdAsync(conn, ConsentSql.SelectRequestIdByTicketSql, ticketId, null, ct).ConfigureAwait(false);
        return id == null ? null : await GetConsentRequestAsync(id.Value, ct).ConfigureAwait(false);
    }

    public async Task<ConsentRequest?> GetConsentRequestByTicketIdAsync(string ticketId, TenantId tenantId, CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.ConsentRequests));
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var id = await ConsentSqlCore.FindRequestIdByTicketIdAsync(conn, ConsentSql.SelectRequestIdByTicketAndTenantSql, ticketId, tenantId, ct).ConfigureAwait(false);
        if (id == null) return null;
        var request = await GetConsentRequestAsync(id.Value, ct).ConfigureAwait(false);
        return request != null && request.TenantId == tenantId ? request : null;
    }

    public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) =>
        ActivateConsentAsync(requestId, null, ct);

    public Task ActivateConsentAsync(Guid requestId, Sid? approvedBy, CancellationToken ct = default) =>
        ActivateConsentInternalAsync(requestId, approvedBy, allowPendingAutoApprove: false, ct);

    public Task ActivateConsentForAutoApproveAsync(Guid requestId, CancellationToken ct = default) =>
        ActivateConsentInternalAsync(requestId, null, allowPendingAutoApprove: true, ct);

    private async Task ActivateConsentInternalAsync(Guid requestId, Sid? approvedBy, bool allowPendingAutoApprove, CancellationToken ct)
    {
        var req = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"ConsentRequest {requestId} not found.");

        TenantScope.RequireTenant(req.TenantId, nameof(GovernanceTable.ConsentRequests));
        StoredTenant.RequireWritable(req.TenantId);
        await EnsureMayMaterializeAsync(req.TenantId, req.TableIdentifier, Array.Empty<ConsentRowFilter>(), ct).ConfigureAwait(false);

        var now = TimeProvider.System.GetUtcNow();
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
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }

        await InvalidateTableEpochAsync(req.TableIdentifier, ct).ConfigureAwait(false);
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

    private Task HydrateConsentsBatchAsync(DbConnection conn, List<Consent> consents, CancellationToken ct) =>
        ConsentSqlCore.HydrateConsentsBatchAsync(conn, consents, ct);

    private async Task<IReadOnlyCollection<string>> ResolveDataOwnerIdsInternalAsync(IEnumerable<string> identifiers, CancellationToken ct)
    {
        const string resolveDataOwnerIdsSql = @"SELECT id FROM DATA_OWNERS WHERE LOWER(ad_sid) = LOWER(@p) OR LOWER(ad_account) = LOWER(@p) OR LOWER(email) = LOWER(@p)";
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var identifier in identifiers.Where(i => !string.IsNullOrWhiteSpace(i)))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = resolveDataOwnerIdsSql;
            cmd.AddBound("@p", identifier);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ids.Add(reader.GetString(0));
            }
        }
        return ids;
    }

    private async Task<IReadOnlyCollection<string>> ResolveRequesterOwnerIdsInternalAsync(ConsentRequest req, CancellationToken ct)
    {
        var identifiers = ConsentApprovalPolicy.IdentifierCandidates(req.RequesterSid.Value).ToList();
        if (req.RequesterIdentifiers != null)
        {
            identifiers.AddRange(req.RequesterIdentifiers);
        }

        return await ResolveDataOwnerIdsInternalAsync(identifiers, ct).ConfigureAwait(false);
    }

    private async Task<bool> IsAuthorizedApproverForTableInternalAsync(TableIdentifier table, Sid approverSid, string? itsmApproverAccount, CancellationToken ct)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await IsAuthorizedApproverForTableInternalAsync(conn, null, table, approverSid, itsmApproverAccount, ct).ConfigureAwait(false);
    }
}
