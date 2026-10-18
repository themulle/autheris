using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Data.Sqlite;

namespace Autheris.Infrastructure.Persistence;

public partial class SqliteGovernanceRepository
{
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
        var consents = new List<Consent>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                       c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                       c.is_revoked, c.tenant_id
                                FROM CONSENTS c
                                JOIN TABLES t ON c.table_id = t.id
                                WHERE t.source_name = @domain COLLATE NOCASE AND t.schema_name = @schema COLLATE NOCASE AND t.table_name = @table COLLATE NOCASE
                                  AND c.is_revoked = 0" + (tenantId != null ? " AND c.tenant_id = @tenantId" : "") + @"
                                  AND c.grantee_type = 'Role' AND c.role_name = @role COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@domain", table.Domain);
            cmd.Parameters.AddWithValue("@schema", table.Schema);
            cmd.Parameters.AddWithValue("@table", table.TableName);
            if (tenantId != null)
            {
                cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
            }

            cmd.Parameters.AddWithValue("@role", roleName);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var consent = new Consent
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    TableId = Guid.Parse(reader.GetString(1)),
                    TableIdentifier = table,
                    ConsentRequestId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                    Effect = Enum.Parse<ConsentEffect>(reader.GetString(3), true),
                    GranteeType = Enum.Parse<GranteeType>(reader.GetString(4), true),
                    GranteeSid = reader.IsDBNull(5) ? (Sid?)null : new Sid(reader.GetString(5)),
                    RoleId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                    RoleName = reader.IsDBNull(7) ? null : reader.GetString(7),
                    ValidFrom = DateTimeOffset.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
                    ValidTo = DateTimeOffset.Parse(reader.GetString(9), System.Globalization.CultureInfo.InvariantCulture),
                    IsRevoked = reader.GetInt32(10) == 1,
                    TenantId = reader.IsDBNull(11) ? TenantId.LegacySingleTenant : new TenantId(reader.GetString(11))
                };
                if (consent.IsActive(atTime))
                {
                    consents.Add(consent);
                }
            }
        }

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
    private async Task<bool> UpdateConsentDescriptionInTransactionAsync(SqliteConnection conn, SqliteTransaction tx, ConsentDescriptionUpdate update, CancellationToken ct)
    {
        // valid_to is stored as ISO text with the writer's offset, so the expiry is compared as a timestamp, not as text.
        string? validTo;
        using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT valid_to FROM CONSENTS WHERE id = @id AND tenant_id = @t AND is_revoked = 0";
            sel.Parameters.AddWithValue("@id", update.ConsentId.ToString());
            sel.Parameters.AddWithValue("@t", update.Tenant.Value);
            validTo = await sel.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }

        if (validTo is null || DateTimeOffset.Parse(validTo, System.Globalization.CultureInfo.InvariantCulture) <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE CONSENTS SET description = @d WHERE id = @id AND tenant_id = @t AND is_revoked = 0";
        cmd.Parameters.AddWithValue("@d", (object?)update.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", update.ConsentId.ToString());
        cmd.Parameters.AddWithValue("@t", update.Tenant.Value);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

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

    /// <summary>Revokes one consent inside <paramref name="tx"/> with the authorisation and audit of <c>RevokeConsentAsync</c>. Returns null when it was already revoked.</summary>
    private async Task<TableIdentifier?> RevokeConsentInTransactionAsync(
        SqliteConnection conn, SqliteTransaction tx, Guid consentId, Sid actor, string reason, CancellationToken ct)
    {
        TableIdentifier? tableId = null;
        var tenant = TenantId.LegacySingleTenant;
        var isGranteeSelf = false;
        var alreadyRevoked = false;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"SELECT t.source_name, t.schema_name, t.table_name, c.tenant_id, c.grantee_type, c.grantee_sid, c.is_revoked
                                FROM CONSENTS c JOIN TABLES t ON c.table_id = t.id WHERE c.id = @id";
            cmd.Parameters.AddWithValue("@id", consentId.ToString());
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                tableId = new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2));
                if (!reader.IsDBNull(3))
                {
                    tenant = new TenantId(reader.GetString(3));
                }

                isGranteeSelf = string.Equals(reader.GetString(4), "User", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(reader.IsDBNull(5) ? null : reader.GetString(5), actor.Value, StringComparison.OrdinalIgnoreCase);
                alreadyRevoked = reader.GetInt32(6) == 1;
            }
        }

        if (!tableId.HasValue)
        {
            throw new KeyNotFoundException($"Consent with ID '{consentId}' does not exist.");
        }

        if (!isGranteeSelf && !await IsAuthorizedApproverForTableInternalAsync(conn, tableId.Value, actor, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException($"User '{actor}' is neither a data owner nor a delegated approver for '{tableId.Value}', nor the beneficiary.");
        }

        if (alreadyRevoked)
        {
            return null;
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE CONSENTS SET is_revoked = 1, revoked_by_sid = @by, revoked_at = @now, revoke_reason = @reason
                                WHERE id = @id AND is_revoked = 0";
            cmd.Parameters.AddWithValue("@id", consentId.ToString());
            cmd.Parameters.AddWithValue("@by", actor.Value);
            cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@reason", reason);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await RecordAuditEventInternalAsync(conn, new AuditLogEntry
        {
            TenantId = tenant,
            EventType = "CONSENT_REVOKED",
            ActorSid = actor,
            TargetTable = tableId.Value.ToString(),
            Decision = "REVOKED",
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new { ConsentId = consentId, Reason = reason })
        }, ct, tx).ConfigureAwait(false);
        return tableId;
    }
}
