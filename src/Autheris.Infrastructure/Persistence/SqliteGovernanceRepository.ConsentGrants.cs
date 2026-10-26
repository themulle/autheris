using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence;

public partial class SqliteGovernanceRepository
{
    /// <summary>Dialect SQL of the consent grant statements (ARCH-MNT-22). Literals are moved verbatim; binding and mapping live in <see cref="ConsentGrantSqlCore"/>.</summary>
    internal static class ConsentGrantSql
    {
        public static string ActiveRoleConsents(bool withTenant) => @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                       c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                       c.is_revoked, c.tenant_id
                                FROM CONSENTS c
                                JOIN TABLES t ON c.table_id = t.id
                                WHERE t.source_name = @domain COLLATE NOCASE AND t.schema_name = @schema COLLATE NOCASE AND t.table_name = @table COLLATE NOCASE
                                  AND c.is_revoked = 0" + (withTenant ? " AND c.tenant_id = @tenantId" : "") + @"
                                  AND c.grantee_type = 'Role' AND c.role_name = @role COLLATE NOCASE";

        public const string RevokeTarget = @"SELECT t.source_name, t.schema_name, t.table_name, c.tenant_id, c.grantee_type, c.grantee_sid, c.is_revoked
                                FROM CONSENTS c JOIN TABLES t ON c.table_id = t.id WHERE c.id = @id";
    }

    public async Task<IReadOnlyList<Consent>> GetActiveRoleConsentsAsync(
        TableIdentifier table,
        string roleName,
        TenantId? tenantId,
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);

        await using var scope = await ReadScopeAsync(ct, snapshot: true).ConfigureAwait(false);
        var conn = scope.Connection;
        var consents = await ConsentGrantSqlCore.ReadActiveRoleConsentsAsync(
            conn, ConsentGrantSql.ActiveRoleConsents(tenantId != null), table, roleName, tenantId, atTime, ct).ConfigureAwait(false);

        await HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        return consents;
    }

    public async Task ReplaceConsentsAsync(
        IReadOnlyList<Guid> revokeConsentIds,
        IReadOnlyList<Consent> create,
        Sid actor,
        string reason,
        IReadOnlyList<AuditLogEntry>? auditEntries = null,
        CancellationToken ct = default) =>
        await ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, auditEntries, null, ct).ConfigureAwait(false);

    public async Task ReplaceConsentsAsync(
        IReadOnlyList<Guid> revokeConsentIds,
        IReadOnlyList<Consent> create,
        Sid actor,
        string reason,
        IReadOnlyList<AuditLogEntry>? auditEntries, IReadOnlyList<ConsentDescriptionUpdate>? descriptionUpdates,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(revokeConsentIds);
        ArgumentNullException.ThrowIfNull(create);
        foreach (var consent in create)
        {
            consent.Validate();
        }

        if (revokeConsentIds.Count == 0 && create.Count == 0 && (descriptionUpdates is null || descriptionUpdates.Count == 0))
        {
            return;
        }

        var tables = new Dictionary<string, TableIdentifier>(StringComparer.OrdinalIgnoreCase);
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        using var tx = conn.BeginTransaction();
        try
        {
            foreach (var id in revokeConsentIds.Distinct())
            {
                var table = await RevokeConsentInTransactionAsync(conn, tx, id, actor, reason, ct).ConfigureAwait(false);
                if (table is { } t)
                {
                    tables[t.ToString()] = t;
                }
            }

            foreach (var consent in create)
            {
                await InsertConsentCoreAsync(conn, tx, consent, ct).ConfigureAwait(false);
                tables[consent.TableIdentifier.ToString()] = consent.TableIdentifier;
            }

            // PLAN-GOV-158-INC1 (SEC-GOV158I1-05/07): description-only updates of non-revoked consents; no epoch effect, no table is added to the bump set.
            foreach (var update in descriptionUpdates ?? [])
            {
                await UpdateConsentDescriptionInTransactionAsync(conn, tx, update, ct).ConfigureAwait(false);
            }

            foreach (var table in tables.Values)
            {
                await IncrementTableEpochInternalAsync(conn, table, tx, ct).ConfigureAwait(false);
            }

            // M2: the caller's audit entries (manifest APPLIED) are part of the same transaction: no change without its record.
            foreach (var entry in auditEntries ?? [])
            {
                await RecordAuditEventInternalAsync(conn, entry, ct, tx).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            OnTransactionCommitted(tx);
        }
        catch (ConsentTenantIntegrityException refusal)
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            OnTransactionRolledBack(tx);
            await RecordTenantRefusalAuditAsync(conn, actor, refusal, ct).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false); // L3: not cancellable, so OnTransactionRolledBack always runs
            OnTransactionRolledBack(tx);
            throw;
        }

        foreach (var table in tables.Values)
        {
            await _epochValidationService.InvalidateEpochAsync(table, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sets <c>CONSENTS.description</c> of one non-revoked, unexpired consent of the tenant inside <paramref name="tx"/>; touches no other column.
    /// Returns false (no row changed) for an unknown, foreign-tenant, revoked or expired consent (SEC-GOV158I1-05).
    /// </summary>
    private Task<bool> UpdateConsentDescriptionInTransactionAsync(SqliteConnection conn, SqliteTransaction tx, ConsentDescriptionUpdate update, CancellationToken ct) =>
        ConsentGrantSqlCore.UpdateDescriptionAsync(conn, tx, update, TimeProvider.System, ct);

    public async Task<bool> UpdateConsentDescriptionWithAuditAsync(Guid consentId, TenantId tenant, string? description, AuditLogEntry audit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        using var tx = conn.BeginTransaction();
        try
        {
            if (!await UpdateConsentDescriptionInTransactionAsync(conn, tx, new ConsentDescriptionUpdate(consentId, tenant, description), ct).ConfigureAwait(false))
            {
                await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
                OnTransactionRolledBack(tx);
                return false;
            }

            await RecordAuditEventInternalAsync(conn, audit, ct, tx).ConfigureAwait(false);
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

    /// <summary>
    /// Revokes one consent: one transaction for the row lock, the approver check, the update, the audit row and the epoch bump (PERS-05), then
    /// the post-commit epoch invalidation. Already revoked is a no-op without audit, epoch bump or eviction.
    /// </summary>
    public async Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        TableIdentifier? revoked;
        await using var scope = await WriteScopeAsync(ct).ConfigureAwait(false);
        var conn = scope.Connection;
        using var tx = conn.BeginTransaction();
        try
        {
            revoked = await RevokeConsentInTransactionAsync(conn, tx, consentId, revokedBySid, reason, ct).ConfigureAwait(false);
            if (revoked is { } table)
            {
                await IncrementTableEpochInternalAsync(conn, table, tx, ct).ConfigureAwait(false);
            }

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

        if (revoked is { } committed)
        {
            await _epochValidationService.InvalidateEpochAsync(committed, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// DEF-SC-02/03: after the refused revoke was rolled back, one audit row in its own short transaction (ids only, never the tenant value).
    /// A failing audit write propagates, so the refusal is never swallowed or downgraded.
    /// </summary>
    private async Task RecordTenantRefusalAuditAsync(SqliteConnection conn, Sid actor, ConsentTenantIntegrityException refusal, CancellationToken ct)
    {
        _logger?.LogWarning("Consent tenant integrity refusal {Code} for consent {ConsentId}.", ConsentTenantIntegrityException.Code, refusal.ConsentId);
        using var tx = conn.BeginTransaction();
        try
        {
            await RecordAuditEventInternalAsync(conn, ConsentTenantRefusalAudit.Build(actor, refusal), ct, tx).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            OnTransactionCommitted(tx);
        }
        catch
        {
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            OnTransactionRolledBack(tx);
            throw;
        }
    }

    /// <summary>Revokes one consent inside <paramref name="tx"/>: row lock/write scope, then approver check, then update and audit. Returns null when it was already revoked.</summary>
    private async Task<TableIdentifier?> RevokeConsentInTransactionAsync(
        SqliteConnection conn, SqliteTransaction tx, Guid consentId, Sid actor, string reason, CancellationToken ct)
    {
        var target = await ConsentGrantSqlCore.ReadRevokeTargetAsync(conn, tx, ConsentGrantSql.RevokeTarget, consentId, actor, ct).ConfigureAwait(false);
        if (target is not { } found)
        {
            throw new KeyNotFoundException($"Consent with ID '{consentId}' does not exist.");
        }

        TableIdentifier? tableId = found.Table;
        if (!found.IsGranteeSelf && !await IsAuthorizedApproverForTableInternalAsync(conn, tx, tableId.Value, actor, null, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException($"User '{actor}' is neither a data owner nor a delegated approver for '{tableId.Value}', nor the beneficiary.");
        }

        if (found.AlreadyRevoked)
        {
            return null;
        }

        var tenant = ConsentGrantSqlCore.ResolveConsentTenant(found.TenantRaw, consentId, tableId.Value.ToString());
        await ConsentGrantSqlCore.MarkRevokedAsync(conn, tx, consentId, actor, reason, TimeProvider.System, ct).ConfigureAwait(false);

        await RecordAuditEventInternalAsync(conn, new AuditLogEntry
        {
            TenantId = tenant,
            EventType = "CONSENT_REVOKED",
            ActorSid = actor,
            TargetTable = tableId.Value.ToString(),
            Decision = ConsentGrantSqlCore.RevokedDecision,
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new { ConsentId = consentId, Reason = reason })
        }, ct, tx).ConfigureAwait(false);
        return tableId;
    }

    /// <summary>DEF-04: the approver read runs on the revoke transaction (<paramref name="tx"/>) after the consent row lock.</summary>
    private async Task<bool> IsAuthorizedApproverForTableInternalAsync(SqliteConnection conn, SqliteTransaction? tx, TableIdentifier table, Sid approverSid, string? itsmApproverAccount, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        if (tx != null)
        {
            cmd.Transaction = tx; // a null assignment would count as an explicit "no transaction" inside a snapshot scope
        }

        cmd.CommandText = @"
            SELECT 1
            FROM TABLE_OWNERS tow
            JOIN DATA_OWNERS o ON tow.data_owner_id = o.id
            JOIN TABLES t ON tow.table_id = t.id
            WHERE t.source_name = @domain COLLATE NOCASE AND t.schema_name = @schema COLLATE NOCASE AND t.table_name = @table COLLATE NOCASE
              AND (o.ad_sid = @apprSid OR (@itsmAccount IS NOT NULL AND (o.ad_account = @itsmAccount COLLATE NOCASE OR o.email = @itsmAccount COLLATE NOCASE))) AND o.is_active = 1
            UNION
            SELECT 1
            FROM DATA_OWNER_DELEGATIONS del
            JOIN TABLE_OWNERS tow ON del.data_owner_id = tow.data_owner_id
            JOIN TABLES t ON tow.table_id = t.id
            WHERE t.source_name = @domain COLLATE NOCASE AND t.schema_name = @schema COLLATE NOCASE AND t.table_name = @table COLLATE NOCASE
              AND (del.delegate_sid = @apprSid OR (@itsmAccount IS NOT NULL AND del.delegate_sid = @itsmAccount)) AND del.valid_from <= @now AND @now < del.valid_to
            UNION
            SELECT 1
            FROM ROLE_MEMBERS rm
            JOIN ROLES r ON rm.role_id = r.id
            WHERE (rm.member_sid = @apprSid OR (@itsmAccount IS NOT NULL AND rm.member_sid = @itsmAccount)) AND r.role_name IN ('GovernanceAdmin', 'ClusterAdmin')
            LIMIT 1;";
        cmd.Parameters.AddWithValue("@domain", table.Domain);
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table", table.TableName);
        cmd.Parameters.AddWithValue("@apprSid", approverSid.Value);
        cmd.Parameters.AddWithValue("@itsmAccount", (object?)itsmApproverAccount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", TimeProvider.System.GetUtcNow().ToString("O"));

        var result = await cmd.ExecuteScalarAsync(ct);
        return result != null && result != DBNull.Value;
    }
}
