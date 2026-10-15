using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Npgsql;

namespace Autheris.Infrastructure.Persistence;

public partial class PostgreSqlGovernanceRepository
{
    public async Task<IReadOnlyList<Consent>> GetActiveRoleConsentsAsync(
        TableIdentifier table,
        string roleName,
        TenantId? tenantId,
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);

        var consents = new List<Consent>();
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                       c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                       c.is_revoked, c.tenant_id
                                FROM CONSENTS c
                                JOIN TABLES t ON c.table_id = t.id
                                WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table)
                                  AND c.is_revoked = 0" + (tenantId != null ? " AND c.tenant_id = @tenantId" : "") + @"
                                  AND c.grantee_type = 'Role' AND LOWER(c.role_name) = LOWER(@role)";
            cmd.Parameters.AddWithValue("@domain", table.Domain);
            cmd.Parameters.AddWithValue("@schema", table.Schema);
            cmd.Parameters.AddWithValue("@table", table.TableName);
            if (tenantId != null)
            {
                cmd.Parameters.AddWithValue("@tenantId", tenantId.Value.Value);
            }

            cmd.Parameters.AddWithValue("@role", roleName);

            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
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
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(revokeConsentIds);
        ArgumentNullException.ThrowIfNull(create);
        foreach (var consent in create)
        {
            consent.Validate();
        }

        if (revokeConsentIds.Count == 0 && create.Count == 0)
        {
            return;
        }

        var tables = new Dictionary<string, TableIdentifier>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
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

            foreach (var table in tables.Values)
            {
                await IncrementTableEpochInternalAsync(conn, tx, table, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            OnTransactionCommitted(tx);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            OnTransactionRolledBack(tx);
            throw;
        }

        foreach (var table in tables.Values)
        {
            _metadataCache.TryRemove(table.ToString().ToLowerInvariant(), out _);
            await _epochValidationService.InvalidateEpochAsync(table, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Revokes one consent inside <paramref name="tx"/> with the authorisation and audit of <c>RevokeConsentAsync</c>. Returns null when it was already revoked.</summary>
    private async Task<TableIdentifier?> RevokeConsentInTransactionAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid consentId, Sid actor, string reason, CancellationToken ct)
    {
        TableIdentifier? tableId = null;
        var tenant = TenantId.LegacySingleTenant;
        var isGranteeSelf = false;
        var alreadyRevoked = false;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"SELECT t.source_name, t.schema_name, t.table_name, c.tenant_id, c.grantee_type, c.grantee_sid, c.is_revoked
                                FROM CONSENTS c 
                                JOIN TABLES t ON c.table_id = t.id
                                WHERE c.id = @id
                                FOR UPDATE OF c";
            cmd.Parameters.AddWithValue("@id", consentId.ToString());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                tableId = new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2));
                if (!reader.IsDBNull(3) && TenantId.TryParse(reader.GetString(3), out var tid))
                {
                    tenant = tid;
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

        if (!isGranteeSelf && !await IsAuthorizedApproverForTableInternalAsync(tableId.Value, actor, null, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException($"User '{actor}' is neither a data owner nor a delegated approver for '{tableId.Value}', nor the beneficiary.");
        }

        if (alreadyRevoked)
        {
            return null;
        }

        await using (var cmd = conn.CreateCommand())
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
