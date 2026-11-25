using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// Standalone dialect-neutral persistence store for atomic single-decision CAS updates, pending requester caps,
/// active unmask grant reads, and data grant rule hydration.
/// </summary>
public sealed class PermissionRequestStore(
    IGovernanceSqlDialect dialect,
    TimeProvider? timeProvider = null) : IConsentExtrasWriter
{
    private const int SubjectBatchSize = 100;

    private static readonly string LockRequestPgSql =
        @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                 r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                 t.source_name, t.schema_name, t.table_name, r.tenant_id,
                 r.requester_identifiers_json, r.context_json, r.requested_write_rules_json,
                 " + ConsentWorkflowSql.RequestColumns + @"
          FROM CONSENT_REQUESTS r
          LEFT JOIN TABLES t ON r.table_id = t.id
          WHERE r.id = @id
          FOR UPDATE OF r";

    private static readonly string LockRequestSsSql =
        @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                 r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                 t.source_name, t.schema_name, t.table_name, r.tenant_id,
                 r.requester_identifiers_json, r.context_json, r.requested_write_rules_json,
                 " + ConsentWorkflowSql.RequestColumns + @"
          FROM dbo.CONSENT_REQUESTS r WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
          LEFT JOIN dbo.TABLES t ON r.table_id = t.id
          WHERE r.id = @id";

    private const string CasUpdatePgSql =
        @"UPDATE CONSENT_REQUESTS
          SET status = @status,
              decided_by = @decidedBy,
              decided_at = @decidedAt,
              approver_capacity = @capacity,
              self_grant = @selfGrant,
              requested_valid_to = @approvedValidTo,
              updated_at = @updatedAt,
              version = version + 1
          WHERE id = @id
            AND status IN ('PENDING', 'PENDING_EXTERNAL_APPROVAL', 'PENDING_SECOND_APPROVAL')
            AND version = @expectedVersion";

    private const string CasUpdateSsSql =
        @"UPDATE dbo.CONSENT_REQUESTS
          SET status = @status,
              decided_by = @decidedBy,
              decided_at = @decidedAt,
              approver_capacity = @capacity,
              self_grant = @selfGrant,
              requested_valid_to = @approvedValidTo,
              updated_at = @updatedAt,
              version = version + 1
          WHERE id = @id
            AND status IN ('PENDING', 'PENDING_EXTERNAL_APPROVAL', 'PENDING_SECOND_APPROVAL')
            AND version = @expectedVersion";

    // Issue #1121: the first of two approvals of a grant on secret data records a step and moves the request to PENDING_SECOND_APPROVAL
    // under the same version CAS; it records no decider, no capacity and no grant. The approved end date is kept for the second decider.
    private const string StepCasUpdatePgSql =
        @"UPDATE CONSENT_REQUESTS
          SET status = @status,
              requested_valid_to = @approvedValidTo,
              updated_at = @updatedAt,
              version = version + 1
          WHERE id = @id
            AND status IN ('PENDING', 'PENDING_EXTERNAL_APPROVAL')
            AND version = @expectedVersion";

    private const string StepCasUpdateSsSql =
        @"UPDATE dbo.CONSENT_REQUESTS
          SET status = @status,
              requested_valid_to = @approvedValidTo,
              updated_at = @updatedAt,
              version = version + 1
          WHERE id = @id
            AND status IN ('PENDING', 'PENDING_EXTERNAL_APPROVAL')
            AND version = @expectedVersion";

    private const string CountPendingPgSql =
        "SELECT COUNT(1) FROM CONSENT_REQUESTS WHERE tenant_id = @tenantId AND requester_sid = @requesterSid AND status IN ('PENDING', 'PENDING_SECOND_APPROVAL', 'PENDING_EXTERNAL_APPROVAL')";

    private const string CountPendingSsSql =
        "SELECT COUNT(1) FROM dbo.CONSENT_REQUESTS WHERE tenant_id = @tenantId AND requester_sid = @requesterSid AND status IN ('PENDING', 'PENDING_SECOND_APPROVAL', 'PENDING_EXTERNAL_APPROVAL')";

    private static readonly string SelectActiveUnmaskPrefixPgSql =
        @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                 c.grantee_sid, c.valid_from, c.valid_to,
                 c.is_revoked, c.revoked_by_sid, c.revoked_at, c.revoke_reason,
                 c.tenant_id, " + ConsentWorkflowSql.ConsentColumns + @"
          FROM CONSENTS c
          JOIN TABLES t ON c.table_id = t.id
          WHERE LOWER(t.source_name) = LOWER(@domain)
            AND LOWER(t.schema_name) = LOWER(@schema)
            AND LOWER(t.table_name) = LOWER(@table)
            AND c.is_revoked = 0
            AND c.grant_kind = 'UNMASK'
            AND c.tenant_id = @tenantId";

    private static readonly string SelectActiveUnmaskPrefixSsSql =
        @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                 c.grantee_sid, c.valid_from, c.valid_to,
                 c.is_revoked, c.revoked_by_sid, c.revoked_at, c.revoke_reason,
                 c.tenant_id, " + ConsentWorkflowSql.ConsentColumns + @"
          FROM dbo.CONSENTS c
          JOIN dbo.TABLES t ON c.table_id = t.id
          WHERE LOWER(t.source_name) = LOWER(@domain)
            AND LOWER(t.schema_name) = LOWER(@schema)
            AND LOWER(t.table_name) = LOWER(@table)
            AND c.is_revoked = 0
            AND c.grant_kind = 'UNMASK'
            AND c.tenant_id = @tenantId";

    private readonly IGovernanceSqlDialect _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private bool IsSqlServer => _dialect is SqlServerGovernanceDialect;

    public async Task<int> CountPendingRequestsForRequesterAsync(
        TenantId tenantId,
        Sid requesterSid,
        CancellationToken ct = default)
    {
        var validTenant = TenantScope.RequireTenant(tenantId, GovernanceTable.ConsentRequests.ToSqlName());
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IsSqlServer ? CountPendingSsSql : CountPendingPgSql;
        cmd.AddBound("@tenantId", validTenant.Value);
        cmd.AddBound("@requesterSid", requesterSid.Value);
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is null or DBNull ? 0 : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<Consent>> GetActiveUnmaskGrantsForSubjectsAsync(
        TenantId tenantId,
        TableIdentifier table,
        IEnumerable<Sid> subjects,
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        var validTenant = TenantScope.RequireTenant(tenantId, GovernanceTable.Consents.ToSqlName());
        ArgumentNullException.ThrowIfNull(subjects);
        var subjectList = subjects.ToList();
        var subjectSet = subjectList.ToHashSet();
        if (subjectSet.Count == 0)
        {
            return [];
        }

        var utcAt = atTime.ToUniversalTime();
        var normalized = ConsentSubjectFilter.DistinctNormalized(subjectList).ToList();
        var results = new List<Consent>();
        var seen = new HashSet<Guid>();

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        for (var offset = 0; offset < normalized.Count; offset += SubjectBatchSize)
        {
            var chunk = normalized.Skip(offset).Take(SubjectBatchSize).ToList();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = (IsSqlServer ? SelectActiveUnmaskPrefixSsSql : SelectActiveUnmaskPrefixPgSql)
                + ConsentSubjectFilter.InListPredicate(chunk.Count);
            for (var i = 0; i < chunk.Count; i++)
            {
                cmd.AddBound("@s" + i.ToString(CultureInfo.InvariantCulture), chunk[i]);
            }

            cmd.AddBound("@domain", table.Domain);
            cmd.AddBound("@schema", table.Schema);
            cmd.AddBound("@table", table.TableName);
            cmd.AddBound("@tenantId", validTenant.Value);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            ConsentSqlCore.ReadSubjectConsentRows(reader, table, utcAt, subjectSet, seen, results);
        }

        results.RemoveAll(c => c.GrantKind != PermissionGrantKind.Unmask || !c.IsActive(utcAt));
        return results;
    }

    public async Task PersistDataGrantExtrasAsync(
        IGovernanceTransaction transaction,
        Consent consent,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(consent);
        await ConsentSqlCore.InsertColumnRulesAndRowFiltersAsync(transaction.Connection, transaction.Transaction, consent, ct)
            .ConfigureAwait(false);
        if (consent.WriteRules.Count > 0)
        {
            await ConsentWriteRulesSql.InsertWriteRulesAsync(transaction.Connection, transaction.Transaction, consent, ct)
                .ConfigureAwait(false);
        }
    }

    public async Task<DecisionCasResult> TryDecideAsync(
        Guid requestId,
        int expectedVersion,
        Func<IGovernanceTransaction, ConsentRequest, Task<PermissionDecisionRecord>> decideInTransaction,
        Func<IGovernanceTransaction, PermissionDecisionRecord, ConsentRequest, Task<IReadOnlyList<GrantRef>>> applyEffect,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decideInTransaction);
        ArgumentNullException.ThrowIfNull(applyEffect);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
        var govTx = new DbGovernanceTransaction(conn, tx);

        try
        {
            var req = await ReadLockedRequestAsync(conn, tx, requestId, ct).ConfigureAwait(false);
            if (req is null)
            {
                await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
                return new DecisionCasResult(DecisionCasOutcome.NotFound);
            }

            if (!IsDecidableStatus(req.Status) || req.Version != expectedVersion)
            {
                await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
                return new DecisionCasResult(DecisionCasOutcome.AlreadyDecided, Request: req);
            }

            // Issue #1121: the decision reads the recorded approval steps (with the approvers' identifier forms) under the row lock.
            await ConsentSqlCore.LoadApprovalStepsAsync(conn, tx, req, ct).ConfigureAwait(false);

            var decision = await decideInTransaction(govTx, req).ConfigureAwait(false);

            int affected;
            await using (var casCmd = conn.CreateCommand())
            {
                casCmd.Transaction = tx;
                if (decision.IsFinal)
                {
                    casCmd.CommandText = IsSqlServer ? CasUpdateSsSql : CasUpdatePgSql;
                    casCmd.AddBound("@decidedBy", decision.DecidedBy.Value);
                    casCmd.AddBound("@decidedAt", decision.DecidedAt.ToUniversalTime(), DbType.DateTimeOffset);
                    casCmd.AddBound("@capacity", decision.Capacity.ToSqlString());
                    casCmd.AddBound("@selfGrant", decision.SelfGrant, DbType.Boolean);
                }
                else
                {
                    casCmd.CommandText = IsSqlServer ? StepCasUpdateSsSql : StepCasUpdatePgSql;
                }

                casCmd.AddBound("@status", decision.NewStatus);
                casCmd.AddBound("@approvedValidTo", decision.ApprovedValidTo.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                casCmd.AddBound("@updatedAt", decision.DecidedAt.ToUniversalTime(), DbType.DateTimeOffset);
                casCmd.AddBound("@id", requestId.ToString());
                casCmd.AddBound("@expectedVersion", expectedVersion, DbType.Int32);
                affected = await casCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (affected != 1)
            {
                await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
                return new DecisionCasResult(DecisionCasOutcome.AlreadyDecided, Request: req);
            }

            if (string.Equals(decision.StepDecision, "APPROVED", StringComparison.OrdinalIgnoreCase))
            {
                await ConsentSqlCore.InsertApprovedStepAsync(
                    conn, tx, Guid.NewGuid(), requestId, decision.StepNumber, decision.DecidedBy, decision.DecidedAt, ct,
                    decision.ApproverIdentifiers ?? [])
                    .ConfigureAwait(false);
            }
            else
            {
                await ConsentSqlCore.InsertRejectedStepAsync(
                    conn, tx, requestId, decision.DecidedBy, decision.Reason ?? "Rejected", decision.DecidedAt, ct)
                    .ConfigureAwait(false);
            }

            var updatedSteps = new List<ApprovalStep>(req.ApprovalSteps)
            {
                new()
                {
                    ConsentRequestId = requestId,
                    StepNumber = decision.StepNumber,
                    ApproverSid = decision.DecidedBy,
                    Decision = decision.StepDecision,
                    RejectionReason = string.Equals(decision.StepDecision, "REJECTED", StringComparison.OrdinalIgnoreCase)
                        ? decision.Reason
                        : null,
                    DecidedAt = decision.DecidedAt,
                    ApproverIdentifiers = [.. decision.ApproverIdentifiers ?? []]
                }
            };

            var updatedReq = new ConsentRequest
            {
                Id = req.Id,
                TableId = req.TableId,
                TableIdentifier = req.TableIdentifier,
                RequesterSid = req.RequesterSid,
                RequestedGranteeType = req.RequestedGranteeType,
                RequestedGranteeRef = req.RequestedGranteeRef,
                BusinessJustification = req.BusinessJustification,
                Status = decision.NewStatus,
                RequestedAt = req.RequestedAt,
                RequestedValidFrom = req.RequestedValidFrom,
                RequestedValidTo = decision.ApprovedValidTo,
                UpdatedAt = decision.DecidedAt,
                TenantId = req.TenantId,
                ContextJson = req.ContextJson,
                RequesterIdentifiers = req.RequesterIdentifiers,
                ApprovalSteps = updatedSteps,
                RequestedWriteRules = req.RequestedWriteRules,
                IncludeReadAccess = req.IncludeReadAccess,
                Version = req.Version + 1,
                WorkflowMode = req.WorkflowMode,
                ExternalSystemId = req.ExternalSystemId,
                ExternalReference = req.ExternalReference,
                RequestKind = req.RequestKind,
                GrantKind = req.GrantKind,
                ProfileId = req.ProfileId,
                TargetColumns = req.TargetColumns,
                ContainerKind = req.ContainerKind,
                ContainerId = req.ContainerId,
                ApproverCapacity = decision.IsFinal ? decision.Capacity : req.ApproverCapacity,
                SelfGrant = decision.IsFinal ? decision.SelfGrant : req.SelfGrant,
                BundleId = req.BundleId,
                DecidedBy = decision.IsFinal ? decision.DecidedBy : req.DecidedBy,
                DecidedAt = decision.IsFinal ? decision.DecidedAt : req.DecidedAt
            };

            var createdGrants = await applyEffect(govTx, decision, updatedReq).ConfigureAwait(false);
            if (decision.IsFinal
                && string.Equals(decision.StepDecision, "APPROVED", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(updatedReq.TenantId.Value)
                && !TenantId.IsReserved(updatedReq.TenantId))
            {
                await BumpGovernanceEpochInTxAsync(conn, tx, updatedReq.TenantId.Value, decision.DecidedAt, ct)
                    .ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new DecisionCasResult(DecisionCasOutcome.Applied, updatedReq, createdGrants);
        }
        catch
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsDecidableStatus(string? status) =>
        string.Equals(status, "PENDING", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "PENDING_EXTERNAL_APPROVAL", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, SecretFourEyesRule.PendingSecondApproval, StringComparison.OrdinalIgnoreCase);

    private async Task<ConsentRequest?> ReadLockedRequestAsync(
        DbConnection conn,
        DbTransaction tx,
        Guid requestId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = IsSqlServer ? LockRequestSsSql : LockRequestPgSql;
        cmd.AddBound("@id", requestId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? ConsentSqlCore.ReadFullConsentRequestRow(reader)
            : null;
    }

    private async Task BumpGovernanceEpochInTxAsync(
        DbConnection conn,
        DbTransaction tx,
        string tenantId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = IsSqlServer ? AccessPolicySettingsStore.BumpEpochSsSql : AccessPolicySettingsStore.BumpEpochPgSql;
        cmd.AddBound("@tenantId", tenantId);
        cmd.AddBound("@updatedAt", now.ToUniversalTime(), DbType.DateTimeOffset);
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var conn = _dialect.CreateConnection();
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class DbGovernanceTransaction(DbConnection connection, DbTransaction transaction) : IGovernanceTransaction
    {
        public DbConnection Connection { get; } = connection;
        public DbTransaction Transaction { get; } = transaction;
    }
}
