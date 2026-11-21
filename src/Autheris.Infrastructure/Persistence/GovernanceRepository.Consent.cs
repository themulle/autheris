namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Grants;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

public partial class GovernanceRepository
{
    internal const string SubjectPredicate = " AND (c.grantee_type = 'Role' OR c.grantee_sid_norm IS NULL OR c.grantee_sid_norm = ANY(@subjects))"; // Role rows pass on purpose: the reader throws on them before the step has run.

    private const int ConsentHydrateBatchSize = 500;

    internal static class ConsentSql
    {
        public const string FindTableIdSql =
            "SELECT id FROM TABLES WHERE LOWER(source_name) = LOWER(@domain) AND LOWER(schema_name) = LOWER(@schema) AND LOWER(table_name) = LOWER(@table)";

        public const string SelectExpiringConsentsSql = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                   c.grantee_sid, c.valid_from, c.valid_to,
                                   c.is_revoked, t.source_name, t.schema_name, t.table_name,
                                   c.tenant_id, c.renewal_count, c.grant_source, c.grant_record_seq, c.profile_assignment_id, c.expiry_notice_sent_at
                            FROM CONSENTS c
                            JOIN TABLES t ON c.table_id = t.id
                            WHERE c.is_revoked = 0 AND c.valid_to > @now AND c.valid_to <= @threshold
                            ORDER BY c.valid_to
                            OFFSET 0 ROWS FETCH NEXT @maxResults ROWS ONLY";

        public const string SelectConsentRequestByIdSql = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                                       r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                                       t.source_name, t.schema_name, t.table_name, r.tenant_id,
                                       r.requester_identifiers_json, r.context_json, r.requested_write_rules_json,
                                       r.version, r.workflow_mode, r.external_system_id, r.external_reference, r.requested_valid_from, r.updated_at, r.request_kind, r.profile_id
                                FROM CONSENT_REQUESTS r
                                LEFT JOIN TABLES t ON r.table_id = t.id
                                WHERE r.id = @id";

        public const string SelectRequestsByRequesterBaseSql = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                                       r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                                       t.source_name, t.schema_name, t.table_name, r.context_json, r.requested_write_rules_json,
                                       r.version, r.workflow_mode, r.external_system_id, r.external_reference, r.requested_valid_from, r.updated_at, r.request_kind, r.profile_id, r.tenant_id
                                FROM CONSENT_REQUESTS r
                                LEFT JOIN TABLES t ON r.table_id = t.id
                                WHERE r.requester_sid = @reqSid
                                  AND r.tenant_id = @tenantId";

        public const string SelectPendingRequestsForApproverSql = @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                                   r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                                   t.source_name, t.schema_name, t.table_name, r.context_json, r.requested_write_rules_json,
                                   r.version, r.workflow_mode, r.external_system_id, r.external_reference, r.requested_valid_from, r.updated_at, r.request_kind, r.profile_id, r.tenant_id
                            FROM CONSENT_REQUESTS r
                            LEFT JOIN TABLES t ON r.table_id = t.id
                            WHERE r.status IN ('PENDING', 'PENDING_SECOND_APPROVAL')
                              AND r.tenant_id = @tenantId
                              AND (r.table_id IN (
                                  SELECT tow.table_id
                                  FROM TABLE_OWNERS tow
                                  JOIN DATA_OWNERS o ON tow.data_owner_id = o.id
                                  WHERE o.ad_sid = @apprSid AND o.is_active = 1
                                  UNION
                                  SELECT tow.table_id
                                  FROM DATA_OWNER_DELEGATIONS del
                                  JOIN TABLE_OWNERS tow ON del.data_owner_id = tow.data_owner_id
                                  WHERE del.delegate_sid = @apprSid AND del.valid_from <= @now AND @now < del.valid_to
                              ) OR r.id IN (
                                  SELECT pai.request_id
                                  FROM PROFILE_ASSIGNMENT_ITEMS pai
                                  WHERE pai.owner_decision = 'PENDING'
                                    AND (pai.owner_sid = @apprSid
                                         OR pai.owner_sid IN (
                                             SELECT o.ad_sid
                                             FROM DATA_OWNER_DELEGATIONS del
                                             JOIN DATA_OWNERS o ON del.data_owner_id = o.id
                                             WHERE del.delegate_sid = @apprSid AND del.valid_from <= @now AND @now < del.valid_to
                                         ))
                              ))";

        public const string CountConfiguredOwnersSql = @"SELECT COUNT(*)
                            FROM TABLES t
                            JOIN TABLE_OWNERS tow ON t.id = tow.table_id
                            WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table)";

        public const string SelectWorkflowRoutesByTenantSql =
            @"SELECT tenant_id, scope_kind, scope_ref, mode, external_system_id, max_duration_days,
                     request_timeout_days, expiry_notice_lead_hours, require_resolvable_approver, updated_by, updated_at
              FROM CONSENT_WORKFLOW_ROUTES
              WHERE tenant_id = @tenant";

        public const string SelectExternalWorkflowSystemByIdSql =
            @"SELECT id, tenant_id, name, client_id, webhook_url, webhook_secret_protected, enabled, created_by, created_at, disabled_at
              FROM EXTERNAL_WORKFLOW_SYSTEMS
              WHERE id = @id AND tenant_id = @tenant";

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

    // Transitional: StartsWith("WITH") engine sniffing goes away when lock clauses live in the dialect.
    private string SelectRequestStatusForUpdateSql =>
        _dialect.LockRowsForUpdate.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
            ? "SELECT status FROM CONSENT_REQUESTS WITH (UPDLOCK, ROWLOCK, HOLDLOCK) WHERE id = @id"
            : "SELECT status FROM CONSENT_REQUESTS WHERE id = @id FOR UPDATE";

    private string SelectSystemRevokeTargetSql =>
        BuildLockRowsQuery("t.source_name, t.schema_name, t.table_name, c.tenant_id, c.table_id, c.effect, c.grantee_type, c.grantee_sid, c.grant_source, c.profile_assignment_id, c.valid_from, c.valid_to", "CONSENTS c", "JOIN TABLES t ON c.table_id = t.id WHERE c.id = @id AND c.consent_request_id = @reqId AND c.is_revoked = 0", null);

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
        var subjectSet = subjectEnumerated.ToHashSet();
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
                                       c.grantee_sid, c.valid_from, c.valid_to,
                                       c.is_revoked, c.revoked_by_sid, c.revoked_at, c.revoke_reason,
                                       c.tenant_id, " + ConsentWorkflowSql.ConsentColumns + @"
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

        await ConsentPatternSql.ReadActivePatternConsentsForDomainAsync(
            conn, table.Domain, tenantId, atTime, subjectSet, seen, consents, ct).ConfigureAwait(false);
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
        AllActiveCoreAsync(subjects, atTime, null, ct);

    public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(
        IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) =>
        AllActiveCoreAsync(subjects, atTime, TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.Consents)), ct);

    private async Task<IReadOnlyList<Consent>> AllActiveCoreAsync(
        IEnumerable<Sid> subjects,
        DateTimeOffset? atTime,
        TenantId? tenantId,
        CancellationToken ct)
    {
        var effectiveAt = atTime ?? TimeProvider.System.GetUtcNow();
        var subjectEnumerated = subjects.ToList();
        var subjectSet = subjectEnumerated.ToHashSet();
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
                                       c.grantee_sid, c.valid_from, c.valid_to,
                                       c.is_revoked, t.source_name, t.schema_name, t.table_name,
                                       c.tenant_id, " + ConsentWorkflowSql.ConsentColumns + @"
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
            ConsentSqlCore.ReadAllSubjectConsentRows(reader, effectiveAt, subjectSet, seen, consents);
        }

        await ConsentPatternSql.ReadActivePatternConsentsForDomainAsync(
            conn, domain: null, tenantId, effectiveAt, subjectSet, seen, consents, ct).ConfigureAwait(false);
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

        if (consent.RowScope.Count > 0 && !consent.IsPatternGrant)
        {
            var tableMeta = await GetTableMetadataAsync(consent.TableIdentifier, ct).ConfigureAwait(false);
            await using var readConn = await OpenConnectionAsync(ct).ConfigureAwait(false);
            var snapshot = await VirtualFilterStore.LoadSnapshotAsync(readConn, ct).ConfigureAwait(false);
            Autheris.Application.Policy.RowScopes.GrantRowScopeValidator.Validate(consent.RowScope, consent.TenantId, snapshot, tableMeta);
        }

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var created = await InsertConsentCoreAsync(conn, tx, consent, ct).ConfigureAwait(false);
            if (consent.IsPatternGrant)
            {
                var newCatalogVersion = await BumpCatalogVersionInternalAsync(conn, tx, 0, 1, ct).ConfigureAwait(false);
                await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
                await PublishCatalogChangedEventAsync(newCatalogVersion, ct).ConfigureAwait(false);
            }
            else
            {
                await IncrementTableEpochInternalAsync(conn, tx, consent.TableIdentifier, ct).ConfigureAwait(false);
                await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
                await InvalidateTableEpochAsync(consent.TableIdentifier, ct).ConfigureAwait(false);
            }

            return created;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }

    private IGrantWriter RequireGrantWriter() =>
        _grantWriter ?? throw new GrantRecordUnavailableException();

    private static Consent BuildPersistedConsent(Consent source, Guid consentId, Guid tableId, long grantRecordSeq) =>
        new()
        {
            Id = consentId,
            TableId = tableId,
            TableIdentifier = source.TableIdentifier,
            ConsentRequestId = source.ConsentRequestId,
            Effect = source.Effect,
            TenantId = source.TenantId,
            GranteeType = source.GranteeType,
            GranteeSid = source.GranteeSid,
            CreatedBySid = source.CreatedBySid,
            ValidFrom = source.ValidFrom,
            ValidTo = source.ValidTo,
            IsRevoked = source.IsRevoked,
            RevokedBySid = source.RevokedBySid,
            RevokedAt = source.RevokedAt,
            RevokeReason = source.RevokeReason,
            RenewalCount = source.RenewalCount,
            GrantSource = source.GrantSource,
            GrantRecordSeq = grantRecordSeq,
            ProfileAssignmentId = source.ProfileAssignmentId,
            ExpiryNoticeSentAt = source.ExpiryNoticeSentAt,
            Description = source.Description,
            ColumnRules = source.ColumnRules,
            RowFilters = source.RowFilters,
            WriteRules = source.WriteRules,
            RowScope = source.RowScope,
            ObjectPattern = source.ObjectPattern,
            DataSource = source.DataSource,
            ColumnSetMode = source.ColumnSetMode,
            WorkflowMode = source.WorkflowMode,
            ProfileItemNo = source.ProfileItemNo,
            ApprovingOwners = source.ApprovingOwners,
            ClassificationAccess = source.ClassificationAccess
        };

    private static GrantRecordInput BuildConsentGrantInput(Consent consent, string tenantId, string requestedBy, string decisionJson, DateTimeOffset decidedAt, GrantRecordType? recordType = null) =>
        new(
            tenantId,
            consent.RowScope.Count == 0
                ? "{}"
                : JsonSerializer.Serialize(new { row_scope = consent.RowScope }),
            requestedBy,
            decisionJson,
            decidedAt,
            ObjectId: consent.IsPatternGrant
                ? consent.ObjectPattern!
                : (!string.IsNullOrWhiteSpace(consent.TableIdentifier.TableName) ? consent.TableIdentifier.ToString() : consent.TableId.ToString("D")),
            GranteeType: consent.GranteeType.ToString(),
            GranteeRef: consent.GranteeSid?.Value,
            RequestId: consent.ConsentRequestId,
            GrantSource: consent.GrantSource,
            ProfileAssignmentId: consent.ProfileAssignmentId,
            ValidFrom: consent.ValidFrom,
            ValidUntil: consent.ValidTo,
            RecordType: recordType);

    private async Task<Consent> InsertConsentCoreAsync(DbConnection conn, DbTransaction tx, Consent consent, CancellationToken ct)
    {
        var grantWriter = RequireGrantWriter();
        var consentId = consent.Id == Guid.Empty ? Guid.NewGuid() : consent.Id;
        var tableId = consent.IsPatternGrant
            ? Guid.Empty
            : await ConsentSqlCore.ResolveTableIdOrThrowAsync(conn, tx, ConsentSql.FindTableIdSql, consent.TableId, consent.TableIdentifier, "consent", ct).ConfigureAwait(false);
        var tenantIdStr = StoredTenant.RequireWritable(consent.TenantId);

        var draft = BuildPersistedConsent(consent, consentId, tableId, consent.GrantRecordSeq);
        var requestedBy = consent.CreatedBySid?.Value ?? consent.GranteeSid?.Value ?? "SYSTEM";
        var now = TimeProvider.System.GetUtcNow();
        var recordType = consent.RenewalCount > 0 ? GrantRecordType.Renewed : GrantRecordType.Issued;
        var input = BuildConsentGrantInput(
            draft,
            tenantIdStr,
            requestedBy,
            JsonSerializer.Serialize(new { Source = consent.GrantSource.ToSqlString(), Effect = consent.Effect.ToString() }),
            now,
            recordType);
        var grantRecord = await grantWriter.InsertAndGrantAsync(tx, draft, input, ct).ConfigureAwait(false);

        var created = BuildPersistedConsent(consent, consentId, tableId, grantRecord.Seq);
        await ConsentSqlCore.InsertColumnRulesAndRowFiltersAsync(conn, tx, created, ct).ConfigureAwait(false);

        var auditEntry = new AuditLogEntry
        {
            TenantId = consent.TenantId,
            EventType = AuditEventTypes.ConsentGranted,
            ActorSid = consent.GranteeSid ?? new Sid("UNKNOWN"),
            TargetTable = consent.IsPatternGrant ? consent.ObjectPattern! : consent.TableIdentifier.ToString(),
            Decision = "ALLOW",
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new
            {
                ConsentId = consentId,
                TableId = tableId,
                consent.Effect,
                Grantee = consent.GranteeSid?.Value,
                Scope = consent.IsPatternGrant ? consent.ObjectPattern! : consent.TableIdentifier.ToString(),
                consent.ValidFrom,
                consent.ValidTo
            })
        };
        await RecordAuditEventAsync(auditEntry, tx, ct).ConfigureAwait(false);
        if (created.WriteRules.Count > 0)
        {
            var actorSid = consent.CreatedBySid ?? consent.GranteeSid ?? new Sid("UNKNOWN");
            await RecordAuditEventAsync(ConsentWriteRulesSql.BuildWriteGrantChangedAudit(created, actorSid), tx, ct).ConfigureAwait(false);
        }

        if (ClearGrantAudit.TryBuild(created) is { } clearAudit) { await RecordAuditEventAsync(clearAudit, tx, ct).ConfigureAwait(false); }

        return created;
    }

    public async Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var consent = await ConsentSqlCore.ReadConsentByIdRowAsync(conn, consentId, ct).ConfigureAwait(false)
            ?? await ConsentPatternSql.ReadPatternConsentByIdRowAsync(conn, consentId, ct).ConfigureAwait(false);
        if (consent != null)
        {
            await HydrateConsentsBatchAsync(conn, [consent], ct).ConfigureAwait(false);
        }
        return consent;
    }

    public async Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        var grantWriter = RequireGrantWriter();
        TableIdentifier? tableId = null;

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var (foundTable, rawTenant, targetConsent) = await ConsentSqlCore.ReadSystemRevokeTargetAsync(
                conn, tx, SelectSystemRevokeTargetSql, consentId, consentRequestId, ct).ConfigureAwait(false);
            if (foundTable is null || targetConsent is null)
            {
                await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
                return false;
            }

            tableId = foundTable;
            var tenant = ConsentGrantSqlCore.ResolveConsentTenant(rawTenant, consentId, tableId.Value.ToString());
            TenantScope.RequireTenant(tenant, nameof(GovernanceTable.Consents));
            StoredTenant.RequireWritable(tenant);

            var now = TimeProvider.System.GetUtcNow();
            var updated = await ConsentSqlCore.ExecuteSystemRevokeUpdateAsync(
                conn, tx, ConsentSql.UpdateSystemRevokeSql, consentId, consentRequestId, revokedBySid, now, reason, bindReqId: false, ct).ConfigureAwait(false);
            if (updated <= 0)
            {
                await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
                return false;
            }

            targetConsent.TenantId = tenant;
            var revokeInput = BuildConsentGrantInput(
                targetConsent,
                tenant.Value,
                revokedBySid.Value,
                JsonSerializer.Serialize(new { Action = "SystemRevoke", Reason = reason }),
                now,
                GrantRecordType.Revoked);
            await grantWriter.RevokeAsync(tx, targetConsent, revokeInput, ct).ConfigureAwait(false);

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
            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
        }
        catch (ConsentTenantIntegrityException refusal)
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            await RecordTenantRefusalAuditAsync(conn, revokedBySid, refusal, ct).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<ConsentRequest>> GetRequestsByRequesterSidAsync(Sid requesterSid, TenantId tenantId, string? status = null, CancellationToken ct = default)
    {
        var validTenant = TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.ConsentRequests));
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ConsentSqlCore.GetRequestsByRequesterSidAsync(conn, ConsentSql.SelectRequestsByRequesterBaseSql, requesterSid, validTenant, status, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForApproverAsync(Sid approverSid, TenantId tenantId, CancellationToken ct = default)
    {
        var validTenant = TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.ConsentRequests));
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ConsentSqlCore.GetPendingRequestsForApproverAsync(conn, ConsentSql.SelectPendingRequestsForApproverSql, approverSid, validTenant, TimeProvider.System.GetUtcNow(), ct).ConfigureAwait(false);
    }

    public Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, CancellationToken ct = default) => ApproveConsentRequestStepAsync(requestId, approverSid, false, ct);

    public async Task<ConsentRequest> ApproveConsentRequestStepAsync(Guid requestId, Sid approverSid, bool requireSecondApprover, CancellationToken ct = default)
    {
        var req = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Request {requestId} not found.");

        TenantScope.RequireTenant(req.TenantId, nameof(GovernanceTable.ConsentRequests));
        StoredTenant.RequireWritable(req.TenantId);

        if (req.WorkflowMode == ConsentWorkflowMode.External)
        {
            throw new InvalidOperationException($"Request {requestId} is in external workflow mode and cannot be approved through internal channels.");
        }

        ConsentApprovalPolicy.EnsureApprovableStatus(requestId, req.Status, ConsentWorkflowMode.Internal);
        var approverOwnerIds = await EnsureApproverIsNotRequesterAsync(req, approverSid, ct).ConfigureAwait(false);

        if (!await IsAuthorizedApproverForTableInternalAsync(req.TableIdentifier, approverSid, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException($"User '{approverSid}' is neither a data owner nor a delegated approver for table '{req.TableIdentifier}'.");
        }

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            string currentStatus = await ConsentSqlCore.ReadRequestStatusOrThrowAsync(conn, tx, SelectRequestStatusForUpdateSql, requestId, ct).ConfigureAwait(false);
            ConsentApprovalPolicy.EnsureApprovableStatus(requestId, currentStatus, ConsentWorkflowMode.Internal);

            var state = await ConsentSqlCore.ReadApprovalStateAsync(
                conn, tx, requestId, approverSid, approverOwnerIds,
                ids => ResolveDataOwnerIdsInternalAsync(ids, ct), ct).ConfigureAwait(false);

            if (state.SameApprover)
            {
                throw new InvalidOperationException(ConsentApprovalPolicy.SameApproverViolationMessage);
            }

            int stepNumber = state.NextStepNumber;
            await ConsentSqlCore.InsertApprovedStepAsync(conn, tx, Guid.NewGuid(), requestId, stepNumber, approverSid, TimeProvider.System.GetUtcNow(), ct).ConfigureAwait(false);

            string newStatus = ConsentApprovalPolicy.StatusAfterApproval(state.RequiresFourEyes || requireSecondApprover, stepNumber, ConsentWorkflowMode.Internal, state.Sensitivity);
            await ConsentSqlCore.ApplyGuardedStatusAsync(conn, tx, ConsentStatusUpdateSeam, requestId, newStatus, currentStatus, ct).ConfigureAwait(false);

            req.Status = newStatus;
            var auditEntry = ConsentApprovalPolicy.BuildStepAudit(req, approverSid, "CONSENT_APPROVAL_STEP", "APPROVED", newStatus, null);
            await RecordAuditEventAsync(auditEntry, tx, ct).ConfigureAwait(false);

            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            return req;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
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
                await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
                return false;
            }

            var req = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false);
            if (req != null)
            {
                req.Status = "WITHDRAWN";
                await RecordAuditEventAsync(
                    ConsentApprovalPolicy.BuildStepAudit(req, requesterSid, AuditEventTypes.ConsentWithdrawn, "WITHDRAWN", "WITHDRAWN", "Access request withdrawn by requester."),
                    tx, ct).ConfigureAwait(false);
            }

            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
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

    public Task<Consent> ExecuteAtomicApprovalWithConsentAsync(Guid requestId, ApprovalStep step, Consent consent, IReadOnlyList<ConsentRowFilter> rowFilters, IReadOnlyList<ConsentColumnRule> columnRules, CancellationToken ct = default) =>
        ExecuteAtomicApprovalWithConsentAsync(requestId, step, consent, rowFilters, columnRules, false, ct);

    public async Task<Consent> ExecuteAtomicApprovalWithConsentAsync(
        Guid requestId,
        ApprovalStep step,
        Consent consent,
        IReadOnlyList<ConsentRowFilter> rowFilters,
        IReadOnlyList<ConsentColumnRule> columnRules,
        bool requireSecondApprover,
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

        if (req.WorkflowMode == ConsentWorkflowMode.External)
        {
            throw new InvalidOperationException($"Request {requestId} is in external workflow mode and cannot be approved through internal channels.");
        }

        var approverOwnerIds = await EnsureApproverIsNotRequesterAsync(req, step.ApproverSid, ct).ConfigureAwait(false);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            string currentStatus = await ConsentSqlCore.ReadRequestStatusOrThrowAsync(conn, tx, SelectRequestStatusForUpdateSql, requestId, ct).ConfigureAwait(false);
            ConsentApprovalPolicy.EnsureApprovableStatus(requestId, currentStatus, ConsentWorkflowMode.Internal);

            var state = await ConsentSqlCore.ReadApprovalStateAsync(
                conn, tx, requestId, step.ApproverSid, approverOwnerIds,
                ids => ResolveDataOwnerIdsInternalAsync(ids, ct), ct).ConfigureAwait(false);

            if (state.SameApprover)
            {
                throw new InvalidOperationException(ConsentApprovalPolicy.SameApproverViolationMessage);
            }

            string newStatus = ConsentApprovalPolicy.StatusAfterApproval(state.RequiresFourEyes || requireSecondApprover, state.NextStepNumber, ConsentWorkflowMode.Internal, state.Sensitivity);
            if (!string.Equals(newStatus, ConsentApprovalPolicy.Approved, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(ConsentApprovalPolicy.FourEyesNotSatisfiedMessage(requestId));
            }

            await ConsentSqlCore.InsertApprovedStepAsync(conn, tx, step.Id, requestId, state.NextStepNumber, step.ApproverSid, step.DecidedAt ?? TimeProvider.System.GetUtcNow(), ct).ConfigureAwait(false);
            await ConsentSqlCore.ApplyGuardedStatusAsync(conn, tx, ConsentStatusUpdateSeam, requestId, newStatus, currentStatus, ct).ConfigureAwait(false);

            req.Status = newStatus;
            await RecordAuditEventAsync(
                ConsentApprovalPolicy.BuildStepAudit(req, step.ApproverSid, "CONSENT_APPROVAL_STEP", "APPROVED", newStatus, null),
                tx, ct).ConfigureAwait(false);

            var createdConsent = await InsertConsentCoreAsync(conn, tx, consent, ct).ConfigureAwait(false);
            await IncrementTableEpochInternalAsync(conn, tx, consent.TableIdentifier, ct).ConfigureAwait(false);
            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);

            await InvalidateTableEpochAsync(consent.TableIdentifier, ct).ConfigureAwait(false);
            return createdConsent;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }

    private ValueTask EnsureMayMaterializeAsync(TenantId tenant, TableIdentifier table, IReadOnlyList<ConsentRowFilter> filters, CancellationToken ct) =>
        _materializationGuard is null
            ? ValueTask.CompletedTask
            : _materializationGuard.EnsureMayMaterializeAsync(tenant, table, filters, ct);

    private async Task<IReadOnlyCollection<string>> EnsureApproverIsNotRequesterAsync(ConsentRequest req, Sid approverSid, CancellationToken ct)
    {
        if (ConsentApprovalPolicy.IsSelfApproval(req, approverSid))
        {
            throw new InvalidOperationException("Separation of duties violated: The requester cannot approve their own request.");
        }

        var approverOwnerIds = await ResolveDataOwnerIdsInternalAsync(ConsentApprovalPolicy.IdentifierCandidates(approverSid.Value), ct).ConfigureAwait(false);
        if (ConsentApprovalPolicy.ShareOwnerId(approverOwnerIds, await ResolveRequesterOwnerIdsInternalAsync(req, ct).ConfigureAwait(false)))
        {
            throw new InvalidOperationException("Separation of duties violated: The requester cannot approve their own request.");
        }

        return approverOwnerIds;
    }

    public async Task<ConsentRequest> RejectConsentRequestAsync(Guid requestId, Sid approverSid, string reason, CancellationToken ct = default)
    {
        var req = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Request {requestId} not found.");

        TenantScope.RequireTenant(req.TenantId, nameof(GovernanceTable.ConsentRequests));
        StoredTenant.RequireWritable(req.TenantId);

        if (req.WorkflowMode == ConsentWorkflowMode.External)
        {
            throw new InvalidOperationException($"Request {requestId} is in external workflow mode and cannot be rejected through internal channels.");
        }

        if (!ConsentApprovalPolicy.IsRejectableStatus(req.Status))
        {
            throw new Autheris.Application.Governance.ConsentRequests.ConsentRequestStatusConflictException(requestId);
        }

        if (!await IsAuthorizedApproverForTableInternalAsync(req.TableIdentifier, approverSid, ct).ConfigureAwait(false))
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
            var auditEntry = ConsentApprovalPolicy.BuildStepAudit(req, approverSid, "CONSENT_REQUEST_REJECTED", "REJECTED", "REJECTED", reason);
            await RecordAuditEventAsync(auditEntry, tx, ct).ConfigureAwait(false);

            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            return req;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }

    public Task ActivateConsentAsync(Guid requestId, CancellationToken ct = default) =>
        ActivateConsentAsync(requestId, null, ct);

    public Task ActivateConsentAsync(Guid requestId, Sid? approvedBy, CancellationToken ct = default) =>
        ActivateConsentInternalAsync(requestId, approvedBy, allowPendingAutoApprove: false, ct);

    public Task ActivateConsentForAutoApproveAsync(Guid requestId, CancellationToken ct = default) =>
        ActivateConsentInternalAsync(requestId, null, allowPendingAutoApprove: true, ct);

    private async Task ActivateConsentInternalAsync(Guid requestId, Sid? approvedBy, bool allowPendingAutoApprove, CancellationToken ct)
    {
        var grantWriter = RequireGrantWriter();
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
            var (claimed, activatedConsentId, consentDraft) = await ConsentSqlCore.TryClaimConsentRequestActivationAsync(conn, tx, req, allowPendingAutoApprove, "ALLOW", now, ct).ConfigureAwait(false);
            if (!claimed || consentDraft is null)
            {
                await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
                return;
            }

            var actorSid = approvedBy ?? req.RequesterSid;
            var grantInput = BuildConsentGrantInput(
                consentDraft,
                req.TenantId.Value,
                actorSid.Value,
                JsonSerializer.Serialize(new { Source = consentDraft.GrantSource.ToSqlString(), Effect = "Allow" }),
                now,
                GrantRecordType.Issued);
            await grantWriter.InsertAndGrantAsync(tx, consentDraft, grantInput, ct).ConfigureAwait(false);
            await ConsentSqlCore.InsertActivatedColumnAndWriteRulesAsync(conn, tx, req, activatedConsentId, ct).ConfigureAwait(false);

            var auditEntry = new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                OccurredAt = now,
                EventType = "CONSENT_GRANTED",
                ActorSid = actorSid,
                TargetTable = req.TableIdentifier.ToString(),
                Decision = "ALLOW",
                TraceId = Guid.NewGuid().ToString(),
                DetailsJson = JsonSerializer.Serialize(new { RequestId = requestId, Grantee = req.RequestedGranteeRef }),
                TenantId = req.TenantId
            };
            await RecordAuditEventAsync(auditEntry, tx, ct).ConfigureAwait(false);
            if (req.RequestedWriteRules.Count > 0)
            {
                var writeConsent = new Consent
                {
                    Id = activatedConsentId,
                    TableId = req.TableId,
                    TableIdentifier = req.TableIdentifier,
                    TenantId = req.TenantId,
                    WriteRules = req.RequestedWriteRules
                };
                await RecordAuditEventAsync(ConsentWriteRulesSql.BuildWriteGrantChangedAudit(writeConsent, actorSid), tx, ct).ConfigureAwait(false);
            }

            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }

        await InvalidateTableEpochAsync(req.TableIdentifier, ct).ConfigureAwait(false);
    }
}
