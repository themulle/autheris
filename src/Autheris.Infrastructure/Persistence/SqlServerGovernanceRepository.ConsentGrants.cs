using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Data.SqlClient;

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

    /// <summary>Revokes one consent inside <paramref name="tx"/> with the authorisation and audit of <c>RevokeConsentAsync</c>. Returns null when it was already revoked.</summary>
    private async Task<TableIdentifier?> RevokeConsentInTransactionAsync(
        SqlConnection conn, SqlTransaction tx, Guid consentId, Sid actor, string reason, CancellationToken ct)
    {
        var target = await ConsentGrantSqlCore.ReadRevokeTargetAsync(conn, tx, ConsentGrantSql.RevokeTarget, consentId, actor, ct).ConfigureAwait(false);
        if (target is not { } found)
        {
            throw new KeyNotFoundException($"Consent with ID '{consentId}' does not exist.");
        }

        TableIdentifier? tableId = found.Table;
        var tenant = TenantId.LegacySingleTenant;
        // D2 (ARCH-MNT-22): PostgreSQL/SQL Server fall back silently to LegacySingleTenant on an unparsable tenant; SQLite throws (follow-up DEF-02).
        if (found.TenantRaw is not null && TenantId.TryParse(found.TenantRaw, out var tid))
        {
            tenant = tid;
        }

        if (!found.IsGranteeSelf && !await IsAuthorizedApproverForTableInternalAsync(tableId.Value, actor, null, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException($"User '{actor}' is neither a data owner nor a delegated approver for '{tableId.Value}', nor the beneficiary.");
        }

        if (found.AlreadyRevoked)
        {
            return null;
        }

        await ConsentGrantSqlCore.MarkRevokedAsync(conn, tx, consentId, actor, reason, TimeProvider.System, ct).ConfigureAwait(false);

        await RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = tenant,
            EventType = "CONSENT_REVOKED",
            ActorSid = actor,
            TargetTable = tableId.Value.ToString(),
            Decision = "REVOKE",
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new { ConsentId = consentId, Reason = reason })
        }, tx, ct).ConfigureAwait(false);
        return tableId;
    }
}
