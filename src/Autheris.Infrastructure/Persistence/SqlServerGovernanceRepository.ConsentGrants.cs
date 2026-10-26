using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Autheris.Infrastructure.Persistence;

public partial class SqlServerGovernanceRepository
{
    /// <summary>Dialect SQL of the consent grant statements (ARCH-MNT-22). Literals are moved verbatim; binding and mapping live in <see cref="ConsentGrantSqlCore"/>.</summary>
    internal static class ConsentGrantSql
    {
        public static string ActiveRoleConsents(bool withTenant) => @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                       c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                       c.is_revoked, c.tenant_id
                                FROM CONSENTS c
                                JOIN TABLES t ON c.table_id = t.id
                                WHERE t.source_name_lc = LOWER(@domain) AND t.schema_name_lc = LOWER(@schema) AND t.table_name_lc = LOWER(@table)
                                  AND c.is_revoked = 0" + (withTenant ? " AND c.tenant_id = @tenantId" : "") + @"
                                  AND c.grantee_type = 'Role' AND LOWER(c.role_name) = LOWER(@role)";

        public const string RevokeTarget = @"SELECT t.source_name, t.schema_name, t.table_name, c.tenant_id, c.grantee_type, c.grantee_sid, c.is_revoked
                                FROM CONSENTS c WITH (UPDLOCK, ROWLOCK)
                                JOIN TABLES t ON c.table_id = t.id
                                WHERE c.id = @id";
    }

    public async Task<IReadOnlyList<Consent>> GetActiveRoleConsentsAsync(
        TableIdentifier table,
        string roleName,
        TenantId? tenantId,
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
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
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
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
                await IncrementTableEpochInternalAsync(conn, tx, table, ct).ConfigureAwait(false);
            }

            // M2: the caller's audit entries (manifest APPLIED) are part of the same transaction: no change without its record.
            foreach (var entry in auditEntries ?? [])
            {
                await RecordAuditEventAsync(entry, tx, ct).ConfigureAwait(false);
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
            await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
            OnTransactionRolledBack(tx);
            throw;
        }

        foreach (var table in tables.Values)
        {
            _metadataCache.TryRemove(table.ToString().ToLowerInvariant(), out _);
            await _epochValidationService.InvalidateEpochAsync(table, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sets <c>CONSENTS.description</c> of one non-revoked, unexpired consent of the tenant inside <paramref name="tx"/>; touches no other column.
    /// Returns false (no row changed) for an unknown, foreign-tenant, revoked or expired consent (SEC-GOV158I1-05).
    /// </summary>
    private Task<bool> UpdateConsentDescriptionInTransactionAsync(SqlConnection conn, SqlTransaction tx, ConsentDescriptionUpdate update, CancellationToken ct) =>
        ConsentGrantSqlCore.UpdateDescriptionAsync(conn, tx, update, TimeProvider.System, ct);

    public async Task<bool> UpdateConsentDescriptionWithAuditAsync(Guid consentId, TenantId tenant, string? description, AuditLogEntry audit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await UpdateConsentDescriptionInTransactionAsync(conn, tx, new ConsentDescriptionUpdate(consentId, tenant, description), ct).ConfigureAwait(false))
            {
                await TransactionRollback.RollbackAsync(tx).ConfigureAwait(false);
                OnTransactionRolledBack(tx);
                return false;
            }

            await RecordAuditEventAsync(audit, tx, ct).ConfigureAwait(false);
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
    /// the post-commit cache eviction. Already revoked is a no-op without audit, epoch bump or eviction.
    /// </summary>
    public async Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        TableIdentifier? revoked;
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            revoked = await RevokeConsentInTransactionAsync(conn, tx, consentId, revokedBySid, reason, ct).ConfigureAwait(false);
            if (revoked is { } table)
            {
                await IncrementTableEpochInternalAsync(conn, tx, table, ct).ConfigureAwait(false);
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
            _metadataCache.TryRemove(committed.ToString().ToLowerInvariant(), out _);
            await _epochValidationService.InvalidateEpochAsync(committed, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// DEF-SC-02/03: after the refused revoke was rolled back, one audit row in its own short transaction (ids only, never the tenant value).
    /// A failing audit write propagates, so the refusal is never swallowed or downgraded.
    /// </summary>
    private async Task RecordTenantRefusalAuditAsync(SqlConnection conn, Sid actor, ConsentTenantIntegrityException refusal, CancellationToken ct)
    {
        _logger?.LogWarning("Consent tenant integrity refusal {Code} for consent {ConsentId}.", ConsentTenantIntegrityException.Code, refusal.ConsentId);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await RecordAuditEventAsync(ConsentTenantRefusalAudit.Build(actor, refusal), tx, ct).ConfigureAwait(false);
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

    /// <summary>Revokes one consent inside <paramref name="tx"/>: row lock, then approver check on the same transaction, then update and audit. Returns null when it was already revoked.</summary>
    private async Task<TableIdentifier?> RevokeConsentInTransactionAsync(
        SqlConnection conn, SqlTransaction tx, Guid consentId, Sid actor, string reason, CancellationToken ct)
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

        await RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = tenant,
            EventType = "CONSENT_REVOKED",
            ActorSid = actor,
            TargetTable = tableId.Value.ToString(),
            Decision = ConsentGrantSqlCore.RevokedDecision,
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new { ConsentId = consentId, Reason = reason })
        }, tx, ct).ConfigureAwait(false);
        return tableId;
    }
}
