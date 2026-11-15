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
using Autheris.Application.ConsentRequests.Events;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

public partial class GovernanceRepository : IConsentWorkflowAdminRepository
{
    public async Task<ConsentWorkflowTableInputs> GetRoutingInputsForTableAsync(
        TenantId tenantId,
        TableIdentifier table,
        CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.ConsentWorkflowRoutes));
        var routes = await ListWorkflowRoutesAsync(tenantId, ct).ConfigureAwait(false);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var ownerIds = await GetTableOwnerIdsForRouteAsync(conn, table, ct).ConfigureAwait(false);
        return new ConsentWorkflowTableInputs(routes, ownerIds);
    }

    public async Task<IReadOnlyList<ConsentWorkflowRoute>> ListWorkflowRoutesAsync(
        TenantId tenantId,
        CancellationToken ct = default)
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
            routes.Add(ReadWorkflowRouteRow(reader));
        }

        return routes;
    }

    public async Task<ConsentWorkflowRoute?> GetWorkflowRouteAsync(
        TenantId tenantId,
        WorkflowRouteScopeKind scopeKind,
        string scopeRef,
        CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.ConsentWorkflowRoutes));
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRef);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tenant_id, scope_kind, scope_ref, mode, external_system_id, max_duration_days,
       request_timeout_days, expiry_notice_lead_hours, require_resolvable_approver, updated_by, updated_at
FROM CONSENT_WORKFLOW_ROUTES
WHERE tenant_id = @tenant AND scope_kind = @scopeKind AND scope_ref = @scopeRef";
        cmd.AddBound("@tenant", tenantId.Value);
        cmd.AddBound("@scopeKind", scopeKind.ToSqlString());
        cmd.AddBound("@scopeRef", scopeRef);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? ReadWorkflowRouteRow(reader)
            : null;
    }

    public async Task<ConsentWorkflowRoute> UpsertWorkflowRouteAsync(
        ConsentWorkflowRoute route,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        var routeTenant = StoredTenant.Parse(route.TenantId).Value;
        TenantScope.RequireTenant(routeTenant, nameof(GovernanceTable.ConsentWorkflowRoutes));
        StoredTenant.RequireWritable(routeTenant);
        var isPg = _dialect is PostgreSqlGovernanceDialect;

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            if (isPg)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO CONSENT_WORKFLOW_ROUTES
(tenant_id, scope_kind, scope_ref, mode, external_system_id, max_duration_days,
 request_timeout_days, expiry_notice_lead_hours, require_resolvable_approver, updated_by, updated_at)
VALUES (@tenant, @scopeKind, @scopeRef, @mode, @externalSystemId, @maxDurationDays,
        @requestTimeoutDays, @expiryNoticeLeadHours, @requireResolvableApprover, @updatedBy, @updatedAt)
ON CONFLICT (tenant_id, scope_kind, scope_ref) DO UPDATE
SET mode = EXCLUDED.mode,
    external_system_id = EXCLUDED.external_system_id,
    max_duration_days = EXCLUDED.max_duration_days,
    request_timeout_days = EXCLUDED.request_timeout_days,
    expiry_notice_lead_hours = EXCLUDED.expiry_notice_lead_hours,
    require_resolvable_approver = EXCLUDED.require_resolvable_approver,
    updated_by = EXCLUDED.updated_by,
    updated_at = EXCLUDED.updated_at";
                BindWorkflowRouteParameters(cmd, route);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await using var updateCmd = conn.CreateCommand();
                updateCmd.Transaction = tx;
                updateCmd.CommandText = @"UPDATE dbo.CONSENT_WORKFLOW_ROUTES
SET mode = @mode,
    external_system_id = @externalSystemId,
    max_duration_days = @maxDurationDays,
    request_timeout_days = @requestTimeoutDays,
    expiry_notice_lead_hours = @expiryNoticeLeadHours,
    require_resolvable_approver = @requireResolvableApprover,
    updated_by = @updatedBy,
    updated_at = @updatedAt
WHERE tenant_id = @tenant AND scope_kind = @scopeKind AND scope_ref = @scopeRef";
                BindWorkflowRouteParameters(updateCmd, route);
                var updated = await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (updated == 0)
                {
                    await using var insertCmd = conn.CreateCommand();
                    insertCmd.Transaction = tx;
                    insertCmd.CommandText = @"INSERT INTO dbo.CONSENT_WORKFLOW_ROUTES
(tenant_id, scope_kind, scope_ref, mode, external_system_id, max_duration_days,
 request_timeout_days, expiry_notice_lead_hours, require_resolvable_approver, updated_by, updated_at)
VALUES (@tenant, @scopeKind, @scopeRef, @mode, @externalSystemId, @maxDurationDays,
        @requestTimeoutDays, @expiryNoticeLeadHours, @requireResolvableApprover, @updatedBy, @updatedAt)";
                    BindWorkflowRouteParameters(insertCmd, route);
                    await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = new TenantId(route.TenantId),
                EventType = AuditEventTypes.ConsentWorkflowConfigChanged,
                ActorSid = new Sid(route.UpdatedBy),
                TargetTable = $"{route.ScopeKind.ToSqlString()}:{route.ScopeRef}",
                Decision = route.Mode.ToSqlString(),
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    scopeKind = route.ScopeKind.ToSqlString(),
                    scopeRef = route.ScopeRef,
                    mode = route.Mode.ToSqlString(),
                    externalSystemId = route.ExternalSystemId,
                    maxDurationDays = route.MaxDurationDays,
                    requestTimeoutDays = route.RequestTimeoutDays,
                    expiryNoticeLeadHours = route.ExpiryNoticeLeadHours
                })
            }, tx, ct).ConfigureAwait(false);

            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            return route;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> DeleteWorkflowRouteAsync(
        TenantId tenantId,
        WorkflowRouteScopeKind scopeKind,
        string scopeRef,
        CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.ConsentWorkflowRoutes));
        StoredTenant.RequireWritable(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeRef);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"DELETE FROM CONSENT_WORKFLOW_ROUTES
WHERE tenant_id = @tenant AND scope_kind = @scopeKind AND scope_ref = @scopeRef";
            cmd.AddBound("@tenant", tenantId.Value);
            cmd.AddBound("@scopeKind", scopeKind.ToSqlString());
            cmd.AddBound("@scopeRef", scopeRef);
            var deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
            if (deleted)
            {
                await RecordAuditEventAsync(new AuditLogEntry
                {
                    TenantId = tenantId,
                    EventType = AuditEventTypes.ConsentWorkflowConfigChanged,
                    ActorSid = new Sid("SYSTEM"),
                    TargetTable = $"{scopeKind.ToSqlString()}:{scopeRef}",
                    Decision = "DELETED",
                    TraceId = Guid.NewGuid().ToString("N"),
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        action = "ROUTE_DELETED",
                        scopeKind = scopeKind.ToSqlString(),
                        scopeRef
                    })
                }, tx, ct).ConfigureAwait(false);
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

    public async Task<ExternalWorkflowSystem> UpsertExternalWorkflowSystemAsync(
        ExternalWorkflowSystem system,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        var systemTenant = StoredTenant.Parse(system.TenantId).Value;
        TenantScope.RequireTenant(systemTenant, nameof(GovernanceTable.ExternalWorkflowSystems));
        StoredTenant.RequireWritable(systemTenant);
        var isPg = _dialect is PostgreSqlGovernanceDialect;

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var ownerCheck = conn.CreateCommand())
            {
                ownerCheck.Transaction = tx;
                ownerCheck.CommandText = "SELECT tenant_id FROM EXTERNAL_WORKFLOW_SYSTEMS WHERE id = @id";
                ownerCheck.AddBound("@id", system.Id.ToString("D"));
                var existingTenant = await ownerCheck.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                if (!string.IsNullOrEmpty(existingTenant) && !string.Equals(existingTenant, system.TenantId, StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException($"External workflow system '{system.Id}' belongs to another tenant.");
                }
            }

            if (isPg)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO EXTERNAL_WORKFLOW_SYSTEMS
(id, tenant_id, name, client_id, webhook_url, webhook_secret_protected, enabled, created_by, created_at, disabled_at)
VALUES (@id, @tenant, @name, @clientId, @webhookUrl, @secret, @enabled, @createdBy, @createdAt, @disabledAt)
ON CONFLICT (id) DO UPDATE
SET name = EXCLUDED.name,
    client_id = EXCLUDED.client_id,
    webhook_url = EXCLUDED.webhook_url,
    webhook_secret_protected = EXCLUDED.webhook_secret_protected,
    enabled = EXCLUDED.enabled,
    disabled_at = EXCLUDED.disabled_at";
                BindExternalSystemParameters(cmd, system);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            else
            {
                await using var updateCmd = conn.CreateCommand();
                updateCmd.Transaction = tx;
                updateCmd.CommandText = @"UPDATE dbo.EXTERNAL_WORKFLOW_SYSTEMS
SET name = @name,
    client_id = @clientId,
    webhook_url = @webhookUrl,
    webhook_secret_protected = @secret,
    enabled = @enabled,
    disabled_at = @disabledAt
WHERE id = @id AND tenant_id = @tenant";
                BindExternalSystemParameters(updateCmd, system);
                var updated = await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (updated == 0)
                {
                    await using var insertCmd = conn.CreateCommand();
                    insertCmd.Transaction = tx;
                    insertCmd.CommandText = @"INSERT INTO dbo.EXTERNAL_WORKFLOW_SYSTEMS
(id, tenant_id, name, client_id, webhook_url, webhook_secret_protected, enabled, created_by, created_at, disabled_at)
VALUES (@id, @tenant, @name, @clientId, @webhookUrl, @secret, @enabled, @createdBy, @createdAt, @disabledAt)";
                    BindExternalSystemParameters(insertCmd, system);
                    await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = new TenantId(system.TenantId),
                EventType = AuditEventTypes.ConsentWorkflowConfigChanged,
                ActorSid = new Sid(system.CreatedBy),
                TargetTable = $"EXTERNAL_SYSTEM:{system.Id}",
                Decision = system.Enabled ? "ENABLED" : "DISABLED",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    systemId = system.Id,
                    name = system.Name,
                    clientId = system.ClientId,
                    enabled = system.Enabled
                })
            }, tx, ct).ConfigureAwait(false);

            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            return system;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<(ExternalWorkflowSystem? System, ConsentRerouteResult RerouteResult)> DisableExternalWorkflowSystemAsync(
        TenantId tenantId,
        Guid systemId,
        string actorSid,
        DateTimeOffset disabledAt,
        CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.ExternalWorkflowSystems));
        StoredTenant.RequireWritable(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorSid);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            ExternalWorkflowSystem? system = null;
            await using (var selectCmd = conn.CreateCommand())
            {
                selectCmd.Transaction = tx;
                selectCmd.CommandText = ConsentSql.SelectExternalWorkflowSystemByIdSql;
                selectCmd.AddBound("@id", systemId.ToString("D"));
                selectCmd.AddBound("@tenant", tenantId.Value);
                await using var reader = await selectCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    system = ReadExternalWorkflowSystemRow(reader);
                }
            }

            if (system is null)
            {
                await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
                return (null, new ConsentRerouteResult(0, 0, []));
            }

            await using (var disableCmd = conn.CreateCommand())
            {
                disableCmd.Transaction = tx;
                disableCmd.CommandText = @"UPDATE EXTERNAL_WORKFLOW_SYSTEMS
SET enabled = @enabled, disabled_at = COALESCE(disabled_at, @disabledAt)
WHERE id = @id AND tenant_id = @tenant";
                disableCmd.AddBound("@enabled", false, System.Data.DbType.Boolean);
                disableCmd.AddBound("@disabledAt", disabledAt.ToUniversalTime(), System.Data.DbType.DateTimeOffset);
                disableCmd.AddBound("@id", systemId.ToString("D"));
                disableCmd.AddBound("@tenant", tenantId.Value);
                await disableCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var reroute = await RerouteOpenRequestsInTransactionAsync(
                conn,
                tx,
                tenantId,
                fromSystemId: systemId,
                fromMode: null,
                targetMode: ConsentWorkflowMode.Internal,
                targetSystemId: null,
                actorSid: actorSid,
                occurredAt: disabledAt,
                requestIds: null,
                rejectInternalWithStep: false,
                ct: ct).ConfigureAwait(false);

            await RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = AuditEventTypes.ConsentWorkflowConfigChanged,
                ActorSid = new Sid(actorSid),
                TargetTable = $"EXTERNAL_SYSTEM:{systemId}",
                Decision = "DISABLED",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    action = "SYSTEM_DISABLED",
                    systemId,
                    reroutedCount = reroute.ReroutedCount
                })
            }, tx, ct).ConfigureAwait(false);

            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            var updatedSystem = system with { Enabled = false, DisabledAt = system.DisabledAt ?? disabledAt };
            return (updatedSystem, reroute);
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ConsentRerouteResult> RerouteOpenRequestsAsync(
        TenantId tenantId,
        Guid? fromSystemId,
        ConsentWorkflowMode? fromMode,
        ConsentWorkflowMode targetMode,
        Guid? targetSystemId,
        string actorSid,
        DateTimeOffset occurredAt,
        IReadOnlyList<Guid>? requestIds = null,
        CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.ConsentRequests));
        StoredTenant.RequireWritable(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorSid);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await RerouteOpenRequestsInTransactionAsync(
                conn,
                tx,
                tenantId,
                fromSystemId,
                fromMode,
                targetMode,
                targetSystemId,
                actorSid,
                occurredAt,
                requestIds,
                rejectInternalWithStep: true,
                ct).ConfigureAwait(false);

            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ConsentRerouteResult> RerouteOpenRequestsInTransactionAsync(
        DbConnection conn,
        DbTransaction tx,
        TenantId tenantId,
        Guid? fromSystemId,
        ConsentWorkflowMode? fromMode,
        ConsentWorkflowMode targetMode,
        Guid? targetSystemId,
        string actorSid,
        DateTimeOffset occurredAt,
        IReadOnlyList<Guid>? requestIds,
        bool rejectInternalWithStep,
        CancellationToken ct)
    {
        var filterSet = requestIds is { Count: > 0 } ? requestIds.ToHashSet() : null;
        var candidates = new List<(Guid RequestId, TableIdentifier Table, string Status, int Version, ConsentWorkflowMode CurrentMode, Guid? CurrentSystemId)>();

        await using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.Transaction = tx;
            var sql = @"SELECT r.id, t.source_name, t.schema_name, t.table_name, r.status, r.version, r.workflow_mode, r.external_system_id
FROM CONSENT_REQUESTS r
LEFT JOIN TABLES t ON r.table_id = t.id
WHERE r.tenant_id = @tenant
  AND r.status IN ('PENDING', 'PENDING_SECOND_APPROVAL', 'PENDING_EXTERNAL_APPROVAL')";
            if (fromSystemId.HasValue)
            {
                sql += " AND r.external_system_id = @fromSystemId";
                selectCmd.AddBound("@fromSystemId", fromSystemId.Value.ToString("D"));
            }
            if (fromMode.HasValue)
            {
                sql += " AND r.workflow_mode = @fromMode";
                selectCmd.AddBound("@fromMode", fromMode.Value.ToSqlString());
            }

            selectCmd.CommandText = sql;
            selectCmd.AddBound("@tenant", tenantId.Value);

            await using var reader = await selectCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var reqId = Guid.Parse(reader.GetString(0));
                if (filterSet is not null && !filterSet.Contains(reqId))
                {
                    continue;
                }

                candidates.Add((
                    reqId,
                    reader.IsDBNull(1) ? default : new TableIdentifier(reader.GetString(1), reader.GetString(2), reader.GetString(3)),
                    reader.GetString(4),
                    Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
                    ConsentWorkflowSqlMapping.ParseWorkflowModeOrDefault(reader.GetString(6)),
                    ConsentWorkflowSql.ReadOptionalGuid(reader, 7)));
            }
        }

        if (rejectInternalWithStep)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.CurrentMode != ConsentWorkflowMode.Internal)
                {
                    continue;
                }

                bool hasRecordedStep = string.Equals(candidate.Status, "PENDING_SECOND_APPROVAL", StringComparison.OrdinalIgnoreCase);
                if (!hasRecordedStep)
                {
                    await using var stepCmd = conn.CreateCommand();
                    stepCmd.Transaction = tx;
                    stepCmd.CommandText = "SELECT COUNT(*) FROM APPROVAL_STEPS WHERE consent_request_id = @reqId";
                    stepCmd.AddBound("@reqId", candidate.RequestId.ToString("D"));
                    hasRecordedStep = Convert.ToInt64(await stepCmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
                }

                if (hasRecordedStep)
                {
                    throw new ConsentDecisionException(
                        ConsentDecisionErrorCodes.RerouteNotAllowed,
                        $"Consent request '{candidate.RequestId}' already has a recorded internal approval step and cannot be rerouted.",
                        statusCode: 409);
                }
            }
        }

        var reroutedIds = new List<Guid>(candidates.Count);
        var newStatus = targetMode == ConsentWorkflowMode.External ? "PENDING_EXTERNAL_APPROVAL" : "PENDING";
        var effectiveSystemId = targetMode == ConsentWorkflowMode.External ? targetSystemId : null;

        foreach (var candidate in candidates)
        {
            await using (var updateCmd = conn.CreateCommand())
            {
                updateCmd.Transaction = tx;
                updateCmd.CommandText = @"UPDATE CONSENT_REQUESTS
SET workflow_mode = @targetMode,
    external_system_id = @targetSystemId,
    status = @newStatus,
    version = version + 1,
    updated_at = @now
WHERE id = @id AND tenant_id = @tenant AND status = @expectedStatus";
                updateCmd.AddBound("@targetMode", targetMode.ToSqlString());
                updateCmd.AddBound("@targetSystemId", (object?)effectiveSystemId?.ToString("D") ?? DBNull.Value);
                updateCmd.AddBound("@newStatus", newStatus);
                updateCmd.AddBound("@now", occurredAt.ToUniversalTime(), System.Data.DbType.DateTimeOffset);
                updateCmd.AddBound("@id", candidate.RequestId.ToString("D"));
                updateCmd.AddBound("@tenant", tenantId.Value);
                updateCmd.AddBound("@expectedStatus", candidate.Status);
                if (await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 0)
                {
                    continue;
                }
            }

            reroutedIds.Add(candidate.RequestId);

            await RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenantId,
                EventType = AuditEventTypes.ConsentRequestRerouted,
                ActorSid = new Sid(actorSid),
                TargetTable = candidate.Table.ToString(),
                Decision = targetMode.ToSqlString(),
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    requestId = candidate.RequestId,
                    fromMode = candidate.CurrentMode.ToSqlString(),
                    fromSystemId = candidate.CurrentSystemId,
                    targetMode = targetMode.ToSqlString(),
                    targetSystemId = effectiveSystemId
                })
            }, tx, ct).ConfigureAwait(false);

            var payload = new ConsentThinEventPayload(
                tenantId.Value,
                candidate.RequestId,
                null,
                candidate.Table.ToString(),
                newStatus,
                candidate.Version + 1,
                targetMode.ToSqlString(),
                occurredAt,
                new ConsentEventLinks($"/api/v1/consent/requests/{candidate.RequestId}", "/api/v1/consent/events"));
            await AppendConsentEventInTransactionAsync(
                conn,
                tx,
                new ConsentEventWrite(
                    tenantId.Value,
                    Guid.NewGuid(),
                    ConsentEventTypes.RequestUpdated,
                    candidate.RequestId.ToString("D"),
                    ConsentEventPayloads.Serialize(payload),
                    occurredAt,
                    effectiveSystemId ?? candidate.CurrentSystemId),
                tenantId,
                ct).ConfigureAwait(false);
        }

        return new ConsentRerouteResult(reroutedIds.Count, 0, reroutedIds);
    }

    private static ConsentWorkflowRoute ReadWorkflowRouteRow(DbDataReader reader) =>
        new(
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
            ConsentWorkflowSql.ReadOptionalTimestamp(reader, 10) ?? DateTimeOffset.UnixEpoch);

    private static ExternalWorkflowSystem ReadExternalWorkflowSystemRow(DbDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            StoredTenant.Parse(reader.GetString(1)).Value,
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture) == 1,
            reader.GetString(7),
            ConsentWorkflowSql.ReadOptionalTimestamp(reader, 8) ?? DateTimeOffset.UnixEpoch,
            ConsentWorkflowSql.ReadOptionalTimestamp(reader, 9));

    private static void BindWorkflowRouteParameters(DbCommand cmd, ConsentWorkflowRoute route)
    {
        cmd.AddBound("@tenant", route.TenantId);
        cmd.AddBound("@scopeKind", route.ScopeKind.ToSqlString());
        cmd.AddBound("@scopeRef", route.ScopeRef);
        cmd.AddBound("@mode", route.Mode.ToSqlString());
        cmd.AddBound("@externalSystemId", (object?)route.ExternalSystemId?.ToString("D") ?? DBNull.Value);
        cmd.AddBound("@maxDurationDays", route.MaxDurationDays, System.Data.DbType.Int32);
        cmd.AddBound("@requestTimeoutDays", route.RequestTimeoutDays, System.Data.DbType.Int32);
        cmd.AddBound("@expiryNoticeLeadHours", route.ExpiryNoticeLeadHours, System.Data.DbType.Int32);
        cmd.AddBound("@requireResolvableApprover", route.RequireResolvableApprover, System.Data.DbType.Boolean);
        cmd.AddBound("@updatedBy", route.UpdatedBy);
        cmd.AddBound("@updatedAt", route.UpdatedAt.ToUniversalTime(), System.Data.DbType.DateTimeOffset);
    }

    private static void BindExternalSystemParameters(DbCommand cmd, ExternalWorkflowSystem system)
    {
        cmd.AddBound("@id", system.Id.ToString("D"));
        cmd.AddBound("@tenant", system.TenantId);
        cmd.AddBound("@name", system.Name);
        cmd.AddBound("@clientId", system.ClientId);
        cmd.AddBound("@webhookUrl", system.WebhookUrl);
        cmd.AddBound("@secret", system.WebhookSecretProtected);
        cmd.AddBound("@enabled", system.Enabled, System.Data.DbType.Boolean);
        cmd.AddBound("@createdBy", system.CreatedBy);
        cmd.AddBound("@createdAt", system.CreatedAt.ToUniversalTime(), System.Data.DbType.DateTimeOffset);
        cmd.AddBound("@disabledAt", system.DisabledAt.HasValue ? system.DisabledAt.Value.ToUniversalTime() : DBNull.Value, System.Data.DbType.DateTimeOffset);
    }
}
