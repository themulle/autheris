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
        return new ConsentWorkflowTableInputs(routes);
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
       request_timeout_days, require_resolvable_approver, updated_by, updated_at
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
            if (route.Mode == ConsentWorkflowMode.External && route.ExternalSystemId is { } targetSystemId)
            {
                await EnsureActiveWorkflowTargetAsync(conn, tx, routeTenant, targetSystemId, ct).ConfigureAwait(false);
            }

            if (isPg)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO CONSENT_WORKFLOW_ROUTES
(tenant_id, scope_kind, scope_ref, mode, external_system_id, max_duration_days,
 request_timeout_days, require_resolvable_approver, updated_by, updated_at)
VALUES (@tenant, @scopeKind, @scopeRef, @mode, @externalSystemId, @maxDurationDays,
        @requestTimeoutDays, @requireResolvableApprover, @updatedBy, @updatedAt)
ON CONFLICT (tenant_id, scope_kind, scope_ref) DO UPDATE
SET mode = EXCLUDED.mode,
    external_system_id = EXCLUDED.external_system_id,
    max_duration_days = EXCLUDED.max_duration_days,
    request_timeout_days = EXCLUDED.request_timeout_days,
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
 request_timeout_days, require_resolvable_approver, updated_by, updated_at)
VALUES (@tenant, @scopeKind, @scopeRef, @mode, @externalSystemId, @maxDurationDays,
        @requestTimeoutDays, @requireResolvableApprover, @updatedBy, @updatedAt)";
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
                    requestTimeoutDays = route.RequestTimeoutDays
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
            // The row lock serialises a role change against route upserts and reroutes that lock the same row.
            string? storedRole = null;
            await using (var ownerCheck = conn.CreateCommand())
            {
                ownerCheck.Transaction = tx;
                ownerCheck.CommandText = isPg
                    ? "SELECT tenant_id, system_role FROM EXTERNAL_WORKFLOW_SYSTEMS WHERE id = @id FOR UPDATE"
                    : "SELECT tenant_id, system_role FROM dbo.EXTERNAL_WORKFLOW_SYSTEMS WITH (UPDLOCK, ROWLOCK) WHERE id = @id";
                ownerCheck.AddBound("@id", system.Id.ToString("D"));
                await using var reader = await ownerCheck.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var existingTenant = reader.GetString(0);
                    if (!string.Equals(existingTenant, system.TenantId, StringComparison.Ordinal))
                    {
                        throw new UnauthorizedAccessException($"External workflow system '{system.Id}' belongs to another tenant.");
                    }

                    storedRole = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }

            if (system.Role == ExternalWorkflowSystemRole.Observer)
            {
                await EnsureObserverRoleIsFreeAsync(conn, tx, system, ct).ConfigureAwait(false);
            }
            else if (storedRole is not null && ExternalWorkflowSystemRoleMapping.FromSqlString(storedRole) != ExternalWorkflowSystemRole.Workflow)
            {
                // A system that stops being an observer loses the tenant-wide feed: the feed of a workflow system is the set of its
                // delivery rows, so the rows fanned out while it was an observer are dropped with the role.
                await using var dropDeliveries = conn.CreateCommand();
                dropDeliveries.Transaction = tx;
                dropDeliveries.CommandText = "DELETE FROM CONSENT_EVENT_DELIVERIES WHERE external_system_id = @id";
                dropDeliveries.AddBound("@id", system.Id.ToString("D"));
                await dropDeliveries.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (isPg)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO EXTERNAL_WORKFLOW_SYSTEMS
(id, tenant_id, name, client_id, webhook_url, webhook_secret_protected, enabled, created_by, created_at, disabled_at, system_role)
VALUES (@id, @tenant, @name, @clientId, @webhookUrl, @secret, @enabled, @createdBy, @createdAt, @disabledAt, @systemRole)
ON CONFLICT (id) DO UPDATE
SET name = EXCLUDED.name,
    client_id = EXCLUDED.client_id,
    webhook_url = EXCLUDED.webhook_url,
    webhook_secret_protected = EXCLUDED.webhook_secret_protected,
    enabled = EXCLUDED.enabled,
    disabled_at = EXCLUDED.disabled_at,
    system_role = EXCLUDED.system_role";
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
    disabled_at = @disabledAt,
    system_role = @systemRole
WHERE id = @id AND tenant_id = @tenant";
                BindExternalSystemParameters(updateCmd, system);
                var updated = await updateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (updated == 0)
                {
                    await using var insertCmd = conn.CreateCommand();
                    insertCmd.Transaction = tx;
                    insertCmd.CommandText = @"INSERT INTO dbo.EXTERNAL_WORKFLOW_SYSTEMS
(id, tenant_id, name, client_id, webhook_url, webhook_secret_protected, enabled, created_by, created_at, disabled_at, system_role)
VALUES (@id, @tenant, @name, @clientId, @webhookUrl, @secret, @enabled, @createdBy, @createdAt, @disabledAt, @systemRole)";
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
                    enabled = system.Enabled,
                    role = system.Role.ToSqlString()
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
                // The system row is held with an update lock that still lets the event appends of deciders read it (SQL Server
                // UPDLOCK is compatible with their shared locks); it is written only after the reroute, so the order here is request
                // rows, tenant event lock, system row, as in a decision (request row, event lock, system row read by the append).
                selectCmd.CommandText = _dialect is PostgreSqlGovernanceDialect
                    ? ConsentSql.LockExternalWorkflowSystemByIdPgSql
                    : ConsentSql.LockExternalWorkflowSystemByIdSsSql;
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
                ct: ct,
                disablingSystemId: systemId).ConfigureAwait(false);

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
        CancellationToken ct,
        Guid? disablingSystemId = null)
    {
        if (targetMode == ConsentWorkflowMode.External && targetSystemId is { } externalTargetId)
        {
            await EnsureActiveWorkflowTargetAsync(conn, tx, tenantId, externalTargetId, ct).ConfigureAwait(false);
        }

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
        var rerouted = new List<(Guid RequestId, TableIdentifier Table, string Status, int Version, ConsentWorkflowMode CurrentMode, Guid? CurrentSystemId)>(candidates.Count);
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
            rerouted.Add(candidate);

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
        }

        // The events follow all row updates: the tenant event lock is the last lock of the transaction, as in a decision (request
        // row, then the event lock), so a reroute never holds the event lock while it waits for a request row a decider holds.
        foreach (var candidate in rerouted)
        {
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
                    // a system disabled in this transaction gets no delivery of the events that take its requests away
                    effectiveSystemId ?? (candidate.CurrentSystemId == disablingSystemId ? null : candidate.CurrentSystemId)),
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
            Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture) == 1,
            reader.GetString(8),
            ConsentWorkflowSql.ReadOptionalTimestamp(reader, 9) ?? DateTimeOffset.UnixEpoch);

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
            ConsentWorkflowSql.ReadOptionalTimestamp(reader, 9),
            ExternalWorkflowSystemRoleMapping.FromSqlString(reader.IsDBNull(10) ? null : reader.GetString(10)));

    private static void BindWorkflowRouteParameters(DbCommand cmd, ConsentWorkflowRoute route)
    {
        cmd.AddBound("@tenant", route.TenantId);
        cmd.AddBound("@scopeKind", route.ScopeKind.ToSqlString());
        cmd.AddBound("@scopeRef", route.ScopeRef);
        cmd.AddBound("@mode", route.Mode.ToSqlString());
        cmd.AddBound("@externalSystemId", (object?)route.ExternalSystemId?.ToString("D") ?? DBNull.Value);
        cmd.AddBound("@maxDurationDays", route.MaxDurationDays, System.Data.DbType.Int32);
        cmd.AddBound("@requestTimeoutDays", route.RequestTimeoutDays, System.Data.DbType.Int32);
        cmd.AddBound("@requireResolvableApprover", route.RequireResolvableApprover, System.Data.DbType.Boolean);
        cmd.AddBound("@updatedBy", route.UpdatedBy);
        cmd.AddBound("@updatedAt", route.UpdatedAt.ToUniversalTime(), System.Data.DbType.DateTimeOffset);
    }

    /// <summary>
    /// Issue #1161: an observer is never a route target and never the system of a pending request. Saving a system as an
    /// observer is refused while a route or a pending request references it, inside the upsert transaction.
    /// </summary>
    private static async Task EnsureObserverRoleIsFreeAsync(DbConnection conn, DbTransaction tx, ExternalWorkflowSystem system, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"SELECT
    (SELECT COUNT(*) FROM CONSENT_WORKFLOW_ROUTES WHERE tenant_id = @tenant AND external_system_id = @id)
  + (SELECT COUNT(*) FROM CONSENT_REQUESTS WHERE tenant_id = @tenant AND external_system_id = @id
       AND status IN ('PENDING', 'PENDING_SECOND_APPROVAL', 'PENDING_EXTERNAL_APPROVAL'))";
        cmd.AddBound("@tenant", system.TenantId);
        cmd.AddBound("@id", system.Id.ToString("D"));
        var references = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (references > 0)
        {
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.ExternalSystemRoleConflict,
                "The external workflow system is referenced by a route or a pending request and cannot become an observer.",
                409);
        }
    }

    /// <summary>
    /// Issue #1161: a route or a reroute may only target an enabled workflow system. The check locks the system row inside the
    /// caller's transaction, so a concurrent role change to observer either waits for this transaction or is seen here.
    /// </summary>
    private async Task EnsureActiveWorkflowTargetAsync(DbConnection conn, DbTransaction tx, TenantId tenantId, Guid systemId, CancellationToken ct)
    {
        var isPg = _dialect is PostgreSqlGovernanceDialect;
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = isPg
            ? "SELECT enabled, system_role FROM EXTERNAL_WORKFLOW_SYSTEMS WHERE id = @id AND tenant_id = @tenant FOR UPDATE"
            : "SELECT enabled, system_role FROM dbo.EXTERNAL_WORKFLOW_SYSTEMS WITH (UPDLOCK, ROWLOCK) WHERE id = @id AND tenant_id = @tenant";
        cmd.AddBound("@id", systemId.ToString("D"));
        cmd.AddBound("@tenant", tenantId.Value);

        var isActiveWorkflow = false;
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var enabled = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture) == 1;
                var role = ExternalWorkflowSystemRoleMapping.FromSqlString(reader.IsDBNull(1) ? null : reader.GetString(1));
                isActiveWorkflow = enabled && role == ExternalWorkflowSystemRole.Workflow;
            }
        }

        if (!isActiveWorkflow)
        {
            throw new ConsentDecisionException(
                ConsentDecisionErrorCodes.ExternalSystemRoleConflict,
                "The target external workflow system is missing, disabled, or not a workflow system.",
                409);
        }
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
        cmd.AddBound("@systemRole", system.Role.ToSqlString());
    }
}
