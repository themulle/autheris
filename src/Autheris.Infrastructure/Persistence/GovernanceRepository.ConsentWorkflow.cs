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

public partial class GovernanceRepository : IConsentWorkflowConfigRepository, IConsentWorkflowAdminRepository
{
    public async Task<ConsentRequest> ApproveExternalConsentRequestStepAsync(
        Guid requestId,
        ExternalDecisionAttribution attribution,
        Sid effectiveApproverSid,
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

            string newStatus = ConsentApprovalPolicy.StatusAfterApproval(state.RequiresFourEyes, stepNumber, ConsentWorkflowMode.External, state.Sensitivity);
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
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new ExternalWorkflowSystem(
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
