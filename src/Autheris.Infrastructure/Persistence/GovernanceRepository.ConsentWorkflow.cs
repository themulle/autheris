namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

public partial class GovernanceRepository : IConsentWorkflowConfigRepository, IConsentWorkflowAdminRepository
{
    public Task<ConsentRequest> ApproveExternalConsentRequestStepAsync(
        Guid requestId,
        ExternalDecisionAttribution attribution,
        Sid effectiveApproverSid,
        CancellationToken ct = default)
        => ApproveExternalConsentRequestStepAsync(requestId, attribution, effectiveApproverSid, requireSecondApprover: false, ct);

    public async Task<ConsentRequest> ApproveExternalConsentRequestStepAsync(
        Guid requestId,
        ExternalDecisionAttribution attribution,
        Sid effectiveApproverSid,
        bool requireSecondApprover,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attribution);
        var req = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Request {requestId} not found.");

        TenantScope.RequireTenant(req.TenantId, nameof(GovernanceTable.ConsentRequests));
        StoredTenant.RequireWritable(req.TenantId);

        if (req.WorkflowMode != ConsentWorkflowMode.External)
        {
            throw new InvalidOperationException($"Request {requestId} is in internal workflow mode and cannot be approved through external channels.");
        }

        ConsentApprovalPolicy.EnsureApprovableStatus(requestId, req.Status, ConsentWorkflowMode.External);
        var approverOwnerIds = await EnsureApproverIsNotRequesterAsync(req, effectiveApproverSid, ct).ConfigureAwait(false);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            string currentStatus = await ConsentSqlCore.ReadRequestStatusOrThrowAsync(conn, tx, SelectRequestStatusForUpdateSql, requestId, ct).ConfigureAwait(false);
            ConsentApprovalPolicy.EnsureApprovableStatus(requestId, currentStatus, ConsentWorkflowMode.External);

            var state = await ConsentSqlCore.ReadApprovalStateAsync(
                conn, tx, requestId, effectiveApproverSid, approverOwnerIds,
                ids => ResolveDataOwnerIdsInternalAsync(ids, ct), ct).ConfigureAwait(false);

            if (state.SameApprover)
            {
                throw new InvalidOperationException(ConsentApprovalPolicy.SameApproverViolationMessage);
            }

            int stepNumber = state.NextStepNumber;
            await ConsentSqlCore.InsertApprovedStepAsync(conn, tx, Guid.NewGuid(), requestId, stepNumber, effectiveApproverSid, attribution.DecidedAt, ct).ConfigureAwait(false);

            string newStatus = ConsentApprovalPolicy.StatusAfterApproval(state.RequiresFourEyes || requireSecondApprover, stepNumber, ConsentWorkflowMode.External, state.Sensitivity);
            await ConsentSqlCore.ApplyGuardedStatusAsync(conn, tx, ConsentStatusUpdateSeam, requestId, newStatus, currentStatus, ct).ConfigureAwait(false);

            req.Status = newStatus;
            var auditEntry = ConsentApprovalPolicy.BuildStepAudit(
                req,
                effectiveApproverSid,
                "CONSENT_APPROVAL_STEP",
                "APPROVED",
                newStatus,
                attribution.Justification,
                attribution.ExternalReference,
                attribution.CorrelationId);
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


    public async Task<Consent> ExecuteExternalAtomicApprovalWithConsentAsync(
        Guid requestId,
        ExternalDecisionAttribution attribution,
        ApprovalStep step,
        Consent consent,
        IReadOnlyList<ConsentRowFilter> rowFilters,
        IReadOnlyList<ConsentColumnRule> columnRules,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attribution);
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

        if (req.WorkflowMode != ConsentWorkflowMode.External)
        {
            throw new InvalidOperationException($"Request {requestId} is in internal workflow mode and cannot be approved through external channels.");
        }

        var approverOwnerIds = await EnsureApproverIsNotRequesterAsync(req, step.ApproverSid, ct).ConfigureAwait(false);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            string currentStatus = await ConsentSqlCore.ReadRequestStatusOrThrowAsync(conn, tx, SelectRequestStatusForUpdateSql, requestId, ct).ConfigureAwait(false);
            ConsentApprovalPolicy.EnsureApprovableStatus(requestId, currentStatus, ConsentWorkflowMode.External);

            var state = await ConsentSqlCore.ReadApprovalStateAsync(
                conn, tx, requestId, step.ApproverSid, approverOwnerIds,
                ids => ResolveDataOwnerIdsInternalAsync(ids, ct), ct).ConfigureAwait(false);

            if (state.SameApprover)
            {
                throw new InvalidOperationException(ConsentApprovalPolicy.SameApproverViolationMessage);
            }

            var approvers = attribution.EffectiveApprovers;
            int stepNumber = state.NextStepNumber;
            if (ConsentApprovalPolicy.RequiresFourEyesApproval(state.RequiresFourEyes, state.Sensitivity) && stepNumber < 2 && approvers.Count >= 2)
            {
                var firstSid = new Sid(string.IsNullOrWhiteSpace(approvers[0].ResolvedSid) ? approvers[0].Subject : approvers[0].ResolvedSid!);
                await EnsureApproverIsNotRequesterAsync(req, firstSid, ct).ConfigureAwait(false);
                await ConsentSqlCore.InsertApprovedStepAsync(conn, tx, Guid.NewGuid(), requestId, stepNumber, firstSid, attribution.DecidedAt, ct).ConfigureAwait(false);
                stepNumber++;
            }

            string newStatus = ConsentApprovalPolicy.StatusAfterApproval(state.RequiresFourEyes, stepNumber, ConsentWorkflowMode.External, state.Sensitivity);
            if (!string.Equals(newStatus, ConsentApprovalPolicy.Approved, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(ConsentApprovalPolicy.FourEyesNotSatisfiedMessage(requestId));
            }

            await ConsentSqlCore.InsertApprovedStepAsync(conn, tx, step.Id, requestId, stepNumber, step.ApproverSid, step.DecidedAt ?? attribution.DecidedAt, ct).ConfigureAwait(false);
            await ConsentSqlCore.ApplyGuardedStatusAsync(conn, tx, ConsentStatusUpdateSeam, requestId, newStatus, currentStatus, ct).ConfigureAwait(false);

            req.Status = newStatus;
            await RecordAuditEventAsync(
                ConsentApprovalPolicy.BuildStepAudit(
                    req,
                    step.ApproverSid,
                    "CONSENT_APPROVAL_STEP",
                    "APPROVED",
                    newStatus,
                    attribution.Justification,
                    attribution.ExternalReference,
                    attribution.CorrelationId),
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


    public async Task<ConsentRequest> RejectExternalConsentRequestAsync(
        Guid requestId,
        ExternalDecisionAttribution attribution,
        Sid effectiveApproverSid,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attribution);
        var req = await GetConsentRequestAsync(requestId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Request {requestId} not found.");

        TenantScope.RequireTenant(req.TenantId, nameof(GovernanceTable.ConsentRequests));
        StoredTenant.RequireWritable(req.TenantId);

        if (req.WorkflowMode != ConsentWorkflowMode.External)
        {
            throw new InvalidOperationException($"Request {requestId} is in internal workflow mode and cannot be rejected through external channels.");
        }

        if (!ConsentApprovalPolicy.IsRejectableStatus(req.Status))
        {
            throw new Autheris.Application.Governance.ConsentRequests.ConsentRequestStatusConflictException(requestId);
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

            await ConsentSqlCore.InsertRejectedStepAsync(conn, tx, requestId, effectiveApproverSid, reason, attribution.DecidedAt, ct).ConfigureAwait(false);
            await ConsentSqlCore.ApplyGuardedStatusAsync(conn, tx, ConsentStatusUpdateSeam, requestId, "REJECTED", currentStatus, ct).ConfigureAwait(false);

            req.Status = "REJECTED";
            var auditEntry = ConsentApprovalPolicy.BuildStepAudit(
                req,
                effectiveApproverSid,
                "CONSENT_REQUEST_REJECTED",
                "REJECTED",
                "REJECTED",
                reason,
                attribution.ExternalReference,
                attribution.CorrelationId);
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

    public async Task<ConsentRequest?> GetConsentRequestByExternalReferenceAsync(
        Guid externalSystemId,
        string externalReference,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.ConsentRequests));
        if (string.IsNullOrWhiteSpace(externalReference))
        {
            return null;
        }

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var id = await ConsentSqlCore.FindRequestIdByExternalReferenceAsync(conn, externalSystemId, externalReference, tenantId, ct).ConfigureAwait(false);
        if (id == null) return null;
        var request = await GetConsentRequestAsync(id.Value, ct).ConfigureAwait(false);
        return request != null && request.TenantId == tenantId ? request : null;
    }

    public async Task<ConsentWorkflowRoute?> ResolveWorkflowRouteAsync(TenantId tenantId, TableIdentifier table, CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.ConsentWorkflowRoutes));
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ConsentSql.SelectWorkflowRoutesByTenantSql;
        cmd.AddBound("@tenant", tenantId.Value);

        var routes = new List<ConsentWorkflowRoute>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            routes.Add(new ConsentWorkflowRoute(
                StoredTenant.Parse(reader.GetString(0)).Value,
                ConsentWorkflowSqlMapping.TryParseRouteScopeKind(reader.GetString(1), out var scopeKind) ? scopeKind : WorkflowRouteScopeKind.Tenant,
                reader.GetString(2),
                ConsentWorkflowSqlMapping.ParseWorkflowModeOrDefault(reader.GetString(3)),
                ConsentWorkflowSql.ReadOptionalGuid(reader, 4),
                Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture) == 1,
                reader.GetString(9),
                ConsentWorkflowSql.ReadOptionalTimestamp(reader, 10) ?? TimeProvider.System.GetUtcNow()));
        }

        var dataSourceRoute = routes.FirstOrDefault(
            r => r.ScopeKind == WorkflowRouteScopeKind.DataSource && string.Equals(r.ScopeRef, table.Domain, StringComparison.OrdinalIgnoreCase));
        if (dataSourceRoute is not null)
        {
            return dataSourceRoute;
        }

        if (routes.Any(r => r.ScopeKind == WorkflowRouteScopeKind.DataOwner))
        {
            var tableOwnerIds = await GetTableOwnerIdsForRouteAsync(conn, table, ct).ConfigureAwait(false);
            var dataOwnerRoute = routes.FirstOrDefault(
                r => r.ScopeKind == WorkflowRouteScopeKind.DataOwner && tableOwnerIds.Contains(r.ScopeRef));
            if (dataOwnerRoute is not null)
            {
                return dataOwnerRoute;
            }
        }

        return routes.FirstOrDefault(r => r.ScopeKind == WorkflowRouteScopeKind.Tenant);
    }

    public async Task<ExternalWorkflowSystem?> GetExternalWorkflowSystemAsync(TenantId tenantId, Guid systemId, CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.ExternalWorkflowSystems));
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ConsentSql.SelectExternalWorkflowSystemByIdSql;
        cmd.AddBound("@id", systemId.ToString());
        cmd.AddBound("@tenant", tenantId.Value);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadExternalWorkflowSystem(reader) : null;
    }

    public async Task<ExternalWorkflowSystem?> GetExternalWorkflowSystemByClientIdAsync(TenantId tenantId, string clientId, CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.ExternalWorkflowSystems));
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        const string selectByClientIdSql =
            @"SELECT id, tenant_id, name, client_id, webhook_url, webhook_secret_protected, enabled, created_by, created_at, disabled_at
              FROM EXTERNAL_WORKFLOW_SYSTEMS
              WHERE tenant_id = @tenant AND LOWER(client_id) = LOWER(@clientId)";

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = selectByClientIdSql;
        cmd.AddBound("@tenant", tenantId.Value);
        cmd.AddBound("@clientId", clientId.Trim());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadExternalWorkflowSystem(reader) : null;
    }

    public async Task<ConsentRequestPage> QueryTenantConsentRequestsAsync(ConsentRequestPageQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        TenantScope.RequireTenant(query.TenantId, nameof(GovernanceTable.ConsentRequests));

        var effectiveLimit = Math.Clamp(query.Limit, 1, 200);
        var (cursorUpdatedAt, cursorId) = DecodeConsentRequestCursor(query.Cursor);
        var normalizedStatus = string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim().ToUpperInvariant();
        var externalSystemStr = query.ExternalSystemId?.ToString("D");

        const string selectTenantConsentRequestsPgSql =
            @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                     r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                     t.source_name, t.schema_name, t.table_name, r.tenant_id,
                     r.requester_identifiers_json, r.context_json, r.requested_write_rules_json,
                     r.version, r.workflow_mode, r.external_system_id, r.external_reference, r.requested_valid_from, r.updated_at, r.request_kind, r.profile_id
              FROM CONSENT_REQUESTS r
              LEFT JOIN TABLES t ON r.table_id = t.id
              WHERE r.tenant_id = @tenantId
                AND (@status IS NULL OR UPPER(r.status) = @status)
                AND (@externalSystemId IS NULL OR r.external_system_id = @externalSystemId)
                AND (@updatedSince IS NULL OR COALESCE(r.updated_at, r.requested_at) >= @updatedSince)
                AND (@domain IS NULL OR LOWER(t.source_name) = LOWER(@domain))
                AND (@schema IS NULL OR LOWER(t.schema_name) = LOWER(@schema))
                AND (@tableName IS NULL OR LOWER(t.table_name) = LOWER(@tableName))
                AND (@requesterSid IS NULL OR r.requester_sid = @requesterSid)
                AND (@cursorUpdatedAt IS NULL OR COALESCE(r.updated_at, r.requested_at) < @cursorUpdatedAt OR (COALESCE(r.updated_at, r.requested_at) = @cursorUpdatedAt AND r.id < @cursorId))
              ORDER BY COALESCE(r.updated_at, r.requested_at) DESC, r.id DESC
              LIMIT @take";

        const string selectTenantConsentRequestsSqlServerSql =
            @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                     r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                     t.source_name, t.schema_name, t.table_name, r.tenant_id,
                     r.requester_identifiers_json, r.context_json, r.requested_write_rules_json,
                     r.version, r.workflow_mode, r.external_system_id, r.external_reference, r.requested_valid_from, r.updated_at, r.request_kind, r.profile_id
              FROM CONSENT_REQUESTS r
              LEFT JOIN TABLES t ON r.table_id = t.id
              WHERE r.tenant_id = @tenantId
                AND (@status IS NULL OR UPPER(r.status) = @status)
                AND (@externalSystemId IS NULL OR r.external_system_id = @externalSystemId)
                AND (@updatedSince IS NULL OR COALESCE(r.updated_at, r.requested_at) >= @updatedSince)
                AND (@domain IS NULL OR LOWER(t.source_name) = LOWER(@domain))
                AND (@schema IS NULL OR LOWER(t.schema_name) = LOWER(@schema))
                AND (@tableName IS NULL OR LOWER(t.table_name) = LOWER(@tableName))
                AND (@requesterSid IS NULL OR r.requester_sid = @requesterSid)
                AND (@cursorUpdatedAt IS NULL OR COALESCE(r.updated_at, r.requested_at) < @cursorUpdatedAt OR (COALESCE(r.updated_at, r.requested_at) = @cursorUpdatedAt AND r.id < @cursorId))
              ORDER BY COALESCE(r.updated_at, r.requested_at) DESC, r.id DESC
              OFFSET 0 ROWS FETCH NEXT @take ROWS ONLY";

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = _dialect is SqlServerGovernanceDialect
            ? selectTenantConsentRequestsSqlServerSql
            : selectTenantConsentRequestsPgSql;

        cmd.AddBound("@tenantId", query.TenantId.Value);
        cmd.AddBound("@status", (object?)normalizedStatus ?? DBNull.Value, System.Data.DbType.String);
        cmd.AddBound("@externalSystemId", (object?)externalSystemStr ?? DBNull.Value, System.Data.DbType.String);
        cmd.AddBound("@updatedSince", query.UpdatedSince.HasValue ? query.UpdatedSince.Value.ToUniversalTime() : (object)DBNull.Value, System.Data.DbType.DateTimeOffset);
        cmd.AddBound("@domain", string.IsNullOrWhiteSpace(query.Domain) ? (object)DBNull.Value : query.Domain.Trim(), System.Data.DbType.String);
        cmd.AddBound("@schema", string.IsNullOrWhiteSpace(query.Schema) ? (object)DBNull.Value : query.Schema.Trim(), System.Data.DbType.String);
        cmd.AddBound("@tableName", string.IsNullOrWhiteSpace(query.TableName) ? (object)DBNull.Value : query.TableName.Trim(), System.Data.DbType.String);
        cmd.AddBound("@requesterSid", string.IsNullOrWhiteSpace(query.RequesterSid) ? (object)DBNull.Value : query.RequesterSid.Trim(), System.Data.DbType.String);
        cmd.AddBound("@cursorUpdatedAt", cursorUpdatedAt.HasValue ? cursorUpdatedAt.Value.ToUniversalTime() : (object)DBNull.Value, System.Data.DbType.DateTimeOffset);
        cmd.AddBound("@cursorId", cursorId.HasValue ? cursorId.Value.ToString("D") : (object)DBNull.Value, System.Data.DbType.String);
        cmd.AddBound("@take", effectiveLimit + 1, System.Data.DbType.Int32);

        var items = new List<ConsentRequest>(effectiveLimit + 1);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            items.Add(ConsentSqlCore.ReadFullConsentRequestRow(reader));
        }

        string? nextCursor = null;
        if (items.Count > effectiveLimit)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            var lastUpdated = (last.UpdatedAt ?? last.RequestedAt).ToUniversalTime();
            nextCursor = EncodeConsentRequestCursor(lastUpdated, last.Id);
        }

        return new ConsentRequestPage(items, nextCursor);
    }

    private static string EncodeConsentRequestCursor(DateTimeOffset updatedAt, Guid id)
    {
        var raw = string.Concat(updatedAt.ToString("O", CultureInfo.InvariantCulture), "|", id.ToString("D"));
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static (DateTimeOffset? UpdatedAt, Guid? Id) DecodeConsentRequestCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return (null, null);
        }

        try
        {
            var padded = cursor.Trim().Replace('-', '+').Replace('_', '/');
            var mod = padded.Length % 4;
            if (mod > 0)
            {
                padded = padded + new string('=', 4 - mod);
            }

            var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var sep = text.IndexOf('|', StringComparison.Ordinal);
            if (sep <= 0)
            {
                return (null, null);
            }

            if (DateTimeOffset.TryParse(text.AsSpan(0, sep), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)
                && Guid.TryParse(text.AsSpan(sep + 1), out var id))
            {
                return (ts.ToUniversalTime(), id);
            }
        }
        catch (FormatException)
        {
            // Invalid cursor degrades cleanly to null (start from newest).
        }

        return (null, null);
    }

    private static ExternalWorkflowSystem ReadExternalWorkflowSystem(DbDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            StoredTenant.Parse(reader.GetString(1)).Value,
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture) == 1,
            reader.GetString(7),
            ConsentWorkflowSql.ReadOptionalTimestamp(reader, 8) ?? TimeProvider.System.GetUtcNow(),
            ConsentWorkflowSql.ReadOptionalTimestamp(reader, 9));

    private static async Task<HashSet<string>> GetTableOwnerIdsForRouteAsync(DbConnection conn, TableIdentifier table, CancellationToken ct)
    {
        const string selectTableOwnerIdsSql = @"SELECT DISTINCT tow.data_owner_id
FROM TABLE_OWNERS tow
JOIN TABLES t ON tow.table_id = t.id
WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table)";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = selectTableOwnerIdsSql;
        cmd.AddBound("@domain", table.Domain);
        cmd.AddBound("@schema", table.Schema);
        cmd.AddBound("@table", table.TableName);

        var ownerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ownerIds.Add(reader.GetString(0));
        }

        return ownerIds;
    }

}
