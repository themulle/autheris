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
        EnsureApproverIsNotRequester(req, effectiveApproverSid);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            string currentStatus = await ConsentSqlCore.ReadRequestStatusOrThrowAsync(conn, tx, SelectRequestStatusForUpdateSql, requestId, ct).ConfigureAwait(false);
            ConsentApprovalPolicy.EnsureApprovableStatus(requestId, currentStatus, ConsentWorkflowMode.External);

            var state = await ConsentSqlCore.ReadApprovalStateAsync(
                conn, tx, requestId, effectiveApproverSid, ct).ConfigureAwait(false);

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
                ConsentWorkflowSqlMapping.TryParseRouteScopeKind(reader.GetString(1), out var scopeKind) ? scopeKind : WorkflowRouteScopeKind.Unknown,
                reader.GetString(2),
                ConsentWorkflowSqlMapping.ParseWorkflowModeOrDefault(reader.GetString(3)),
                ConsentWorkflowSql.ReadOptionalGuid(reader, 4),
                Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture) == 1,
                reader.GetString(8),
                ConsentWorkflowSql.ReadOptionalTimestamp(reader, 9) ?? TimeProvider.System.GetUtcNow()));
        }

        // Removed (data owner, data source) and unknown scope kinds are inert; only the tenant route applies here.
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
        var normalizedAdditionalStatus = normalizedStatus is null || string.IsNullOrWhiteSpace(query.AdditionalStatus)
            ? null
            : query.AdditionalStatus.Trim().ToUpperInvariant();
        var externalSystemStr = query.ExternalSystemId?.ToString("D");

        // PostgreSQL: updated_at is TIMESTAMPTZ NOT NULL (step 0015) and requested_at is text, so COALESCE over both cannot be typed;
        // the page orders by updated_at alone, which is what the COALESCE yields for every row.
        const string selectTenantConsentRequestsPgSql =
            @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                     r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                     t.source_name, t.schema_name, t.table_name, r.tenant_id,
                     r.requester_identifiers_json, r.context_json, r.requested_write_rules_json,
                     r.version, r.workflow_mode, r.external_system_id, r.external_reference, r.requested_valid_from, r.updated_at, r.request_kind, r.profile_id, r.grant_kind, r.target_columns, r.container_kind, r.container_id, r.approver_capacity, r.self_grant, r.bundle_id, r.decided_by, r.decided_at
              FROM CONSENT_REQUESTS r
              LEFT JOIN TABLES t ON r.table_id = t.id
              WHERE r.tenant_id = @tenantId
                AND (@status IS NULL OR UPPER(r.status) = @status OR UPPER(r.status) = @additionalStatus)
                AND (@externalSystemId IS NULL OR r.external_system_id = @externalSystemId)
                AND (@updatedSince IS NULL OR r.updated_at >= @updatedSince)
                AND (@domain IS NULL OR LOWER(t.source_name) = LOWER(@domain))
                AND (@schema IS NULL OR LOWER(t.schema_name) = LOWER(@schema))
                AND (@tableName IS NULL OR LOWER(t.table_name) = LOWER(@tableName))
                AND (@requesterSid IS NULL OR r.requester_sid = @requesterSid)
                AND (@cursorUpdatedAt IS NULL OR r.updated_at < @cursorUpdatedAt OR (r.updated_at = @cursorUpdatedAt AND r.id < @cursorId))
              ORDER BY r.updated_at DESC, r.id DESC
              LIMIT @take";

        const string selectTenantConsentRequestsSqlServerSql =
            @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                     r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                     t.source_name, t.schema_name, t.table_name, r.tenant_id,
                     r.requester_identifiers_json, r.context_json, r.requested_write_rules_json,
                     r.version, r.workflow_mode, r.external_system_id, r.external_reference, r.requested_valid_from, r.updated_at, r.request_kind, r.profile_id, r.grant_kind, r.target_columns, r.container_kind, r.container_id, r.approver_capacity, r.self_grant, r.bundle_id, r.decided_by, r.decided_at
              FROM CONSENT_REQUESTS r
              LEFT JOIN TABLES t ON r.table_id = t.id
              WHERE r.tenant_id = @tenantId
                AND (@status IS NULL OR UPPER(r.status) = @status OR UPPER(r.status) = @additionalStatus)
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
        cmd.AddBound("@additionalStatus", (object?)normalizedAdditionalStatus ?? DBNull.Value, System.Data.DbType.String);
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

    private static string EncodeConsentRequestCursor(DateTimeOffset updatedAt, Guid id) =>
        Autheris.Application.Governance.Paging.ConsentRequestCursor.Encode(updatedAt, id);

    private static (DateTimeOffset? UpdatedAt, Guid? Id) DecodeConsentRequestCursor(string? cursor) =>
        Autheris.Application.Governance.Paging.ConsentRequestCursor.TryDecode(cursor, out var ts, out var id)
            ? (ts, id)
            : (null, null);

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

}
