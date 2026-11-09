namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Logging;

public partial class GovernanceRepository
{
    internal static class ConsentGrantPostgreSql
    {
        public static string ActiveRoleConsents(bool withTenant) => @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type,
                                       c.grantee_sid, c.role_id, c.role_name, c.valid_from, c.valid_to,
                                       c.is_revoked, c.tenant_id
                                FROM CONSENTS c
                                JOIN TABLES t ON c.table_id = t.id
                                WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table)
                                  AND c.is_revoked = 0" + (withTenant ? " AND c.tenant_id = @tenantId" : "") + @"
                                  AND c.grantee_type = 'Role' AND LOWER(c.role_name) = LOWER(@role)";

        public const string RevokeTarget = @"SELECT t.source_name, t.schema_name, t.table_name, c.tenant_id, c.grantee_type, c.grantee_sid, c.is_revoked
                                FROM CONSENTS c 
                                JOIN TABLES t ON c.table_id = t.id
                                WHERE c.id = @id
                                FOR UPDATE OF c";
    }

    internal static class ConsentGrantSqlServer
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

    private string RevokeTargetSql => _dialect is PostgreSqlGovernanceDialect
        ? ConsentGrantPostgreSql.RevokeTarget
        : ConsentGrantSqlServer.RevokeTarget;

    private string ActiveRoleConsentsSql(bool withTenant) => _dialect is PostgreSqlGovernanceDialect
        ? ConsentGrantPostgreSql.ActiveRoleConsents(withTenant)
        : ConsentGrantSqlServer.ActiveRoleConsents(withTenant);

    public async Task<IReadOnlyList<Consent>> GetActiveRoleConsentsAsync(
        TableIdentifier table,
        string roleName,
        TenantId? tenantId,
        DateTimeOffset atTime,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.Consents)); // no cross-tenant role read

        var sql = ActiveRoleConsentsSql(tenantId != null);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var consents = await ConsentGrantSqlCore.ReadActiveRoleConsentsAsync(conn, sql, table, roleName, tenantId, atTime, ct).ConfigureAwait(false);
        await HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        return consents;
    }

    public Task ReplaceConsentsAsync(
        IReadOnlyList<Guid> revokeConsentIds,
        IReadOnlyList<Consent> create,
        Sid actor,
        string reason,
        IReadOnlyList<AuditLogEntry>? auditEntries = null,
        CancellationToken ct = default) =>
        ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, auditEntries, null, ct);

    public async Task ReplaceConsentsAsync(
        IReadOnlyList<Guid> revokeConsentIds,
        IReadOnlyList<Consent> create,
        Sid actor,
        string reason,
        IReadOnlyList<AuditLogEntry>? auditEntries,
        IReadOnlyList<ConsentDescriptionUpdate>? descriptionUpdates,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(revokeConsentIds);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        foreach (var c in create)
        {
            ArgumentNullException.ThrowIfNull(c);
            StoredTenant.RequireWritable(c.TenantId);
            TenantScope.RequireTenant(c.TenantId, nameof(GovernanceTable.Consents));
            c.Validate();
        }

        if (descriptionUpdates != null)
        {
            foreach (var u in descriptionUpdates)
            {
                TenantScope.RequireTenant(u.Tenant, nameof(GovernanceTable.Consents));
                StoredTenant.RequireWritable(u.Tenant);
            }
        }

        var tables = new Dictionary<string, TableIdentifier>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var id in revokeConsentIds.Distinct())
            {
                var revoked = await RevokeConsentInTransactionAsync(conn, tx, id, actor, reason, ct).ConfigureAwait(false);
                if (revoked != null)
                {
                    tables[revoked.Value.ToString().ToLowerInvariant()] = revoked.Value;
                }
            }

            foreach (var c in create)
            {
                await InsertConsentCoreAsync(conn, tx, c, ct).ConfigureAwait(false);
                tables[c.TableIdentifier.ToString().ToLowerInvariant()] = c.TableIdentifier;
            }

            if (descriptionUpdates != null)
            {
                foreach (var update in descriptionUpdates)
                {
                    await UpdateConsentDescriptionInTransactionAsync(conn, tx, update, ct).ConfigureAwait(false);
                }
            }

            if (auditEntries != null)
            {
                foreach (var entry in auditEntries)
                {
                    await RecordAuditEventAsync(entry, tx, ct).ConfigureAwait(false);
                }
            }

            foreach (var table in tables.Values)
            {
                await IncrementTableEpochInternalAsync(conn, tx, table, ct).ConfigureAwait(false);
            }

            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
        }
        catch (ConsentTenantIntegrityException refusal)
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            await RecordTenantRefusalAuditAsync(conn, actor, refusal, ct).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }

        foreach (var table in tables.Values)
        {
            _metadataInvalidator?.Invoke(table.ToString().ToLowerInvariant());
            await InvalidateTableEpochAsync(table, ct).ConfigureAwait(false);
        }
    }

    private Task<bool> UpdateConsentDescriptionInTransactionAsync(DbConnection conn, DbTransaction tx, ConsentDescriptionUpdate update, CancellationToken ct) =>
        ConsentGrantSqlCore.UpdateDescriptionAsync(conn, tx, update, TimeProvider.System, ct);

    public async Task<bool> UpdateConsentDescriptionWithAuditAsync(Guid consentId, TenantId tenant, string? description, AuditLogEntry audit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        TenantScope.RequireTenant(tenant, nameof(GovernanceTable.Consents));
        StoredTenant.RequireWritable(tenant);

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await UpdateConsentDescriptionInTransactionAsync(conn, tx, new ConsentDescriptionUpdate(consentId, tenant, description), ct).ConfigureAwait(false))
            {
                await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
                return false;
            }

            await RecordAuditEventAsync(audit, tx, ct).ConfigureAwait(false);
            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        TableIdentifier? revoked;
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            revoked = await RevokeConsentInTransactionAsync(conn, tx, consentId, revokedBySid, reason, ct).ConfigureAwait(false);
            if (revoked is { } table)
            {
                await IncrementTableEpochInternalAsync(conn, tx, table, ct).ConfigureAwait(false);
            }

            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
        }
        catch (ConsentTenantIntegrityException refusal)
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            await RecordTenantRefusalAuditAsync(conn, revokedBySid, refusal, ct).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }

        if (revoked is { } committed)
        {
            await InvalidateTableEpochAsync(committed, ct).ConfigureAwait(false);
        }
    }

    private async Task RecordTenantRefusalAuditAsync(DbConnection conn, Sid actor, ConsentTenantIntegrityException refusal, CancellationToken ct)
    {
        _logger?.LogWarning("Consent tenant integrity refusal {Code} for consent {ConsentId}.", ConsentTenantIntegrityException.Code, refusal.ConsentId);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await RecordAuditEventAsync(ConsentTenantRefusalAudit.Build(actor, refusal), tx, ct).ConfigureAwait(false);
            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<TableIdentifier?> RevokeConsentInTransactionAsync(
        DbConnection conn, DbTransaction tx, Guid consentId, Sid actor, string reason, CancellationToken ct)
    {
        var target = await ConsentGrantSqlCore.ReadRevokeTargetAsync(conn, tx, RevokeTargetSql, consentId, actor, ct).ConfigureAwait(false);
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

        if (!await ConsentGrantSqlCore.MarkRevokedAsync(conn, tx, consentId, actor, reason, TimeProvider.System.GetUtcNow(), ct).ConfigureAwait(false))
        {
            return null;
        }

        await RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = tenant,
            EventType = "CONSENT_REVOKED",
            ActorSid = actor,
            TargetTable = tableId.Value.ToString(),
            Decision = ConsentGrantSqlCore.RevokedDecision,
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                ConsentId = consentId,
                Reason = reason
            })
        }, tx, ct).ConfigureAwait(false);

        return tableId;
    }

    internal async Task<bool> IsAuthorizedApproverForTableInternalAsync(
        DbConnection conn, DbTransaction? tx, TableIdentifier table, Sid approverSid, string? itsmApproverAccount, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"SELECT COUNT(*)
                            FROM TABLES t
                            JOIN TABLE_OWNERS tow ON t.id = tow.table_id
                            JOIN DATA_OWNERS o ON tow.data_owner_id = o.id
                            WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table)
                              AND (o.ad_sid = @apprSid OR (@itsmAccount IS NOT NULL AND (LOWER(o.ad_account) = LOWER(@itsmAccount) OR LOWER(o.email) = LOWER(@itsmAccount))))
                              AND o.is_active = 1";
        cmd.AddBound("@domain", table.Domain);
        cmd.AddBound("@schema", table.Schema);
        cmd.AddBound("@table", table.TableName);
        cmd.AddBound("@apprSid", approverSid.Value);
        cmd.AddBound("@itsmAccount", (object?)itsmApproverAccount ?? DBNull.Value, DbType.String);

        var directOwnerCount = Convert.ToInt64(await ExecuteScalarAsync(cmd, "ConsentGrants.CheckDirectOwner", ct).ConfigureAwait(false));
        if (directOwnerCount > 0) return true;

        await using var delCmd = conn.CreateCommand();
        delCmd.Transaction = tx;
        delCmd.CommandText = @"SELECT COUNT(*)
                               FROM DATA_OWNER_DELEGATIONS d
                               JOIN DATA_OWNERS o ON d.data_owner_id = o.id
                               JOIN TABLE_OWNERS tow ON o.id = tow.data_owner_id
                               JOIN TABLES t ON tow.table_id = t.id
                               WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table)
                                 AND (d.delegate_sid = @apprSid OR (@itsmAccount IS NOT NULL AND LOWER(d.delegate_sid) = LOWER(@itsmAccount)))
                                 AND d.valid_from <= @now AND d.valid_to >= @now";
        delCmd.AddBound("@domain", table.Domain);
        delCmd.AddBound("@schema", table.Schema);
        delCmd.AddBound("@table", table.TableName);
        delCmd.AddBound("@apprSid", approverSid.Value);
        delCmd.AddBound("@itsmAccount", (object?)itsmApproverAccount ?? DBNull.Value, DbType.String);
        delCmd.AddBound("@now", TimeProvider.System.GetUtcNow().ToString("O"));

        var delegateCount = Convert.ToInt64(await ExecuteScalarAsync(delCmd, "ConsentGrants.CheckDelegates", ct).ConfigureAwait(false));
        if (delegateCount > 0) return true;

        await using var roleCmd = conn.CreateCommand();
        roleCmd.Transaction = tx;
        roleCmd.CommandText = @"SELECT COUNT(*)
                                FROM ROLES r
                                JOIN ROLE_MEMBERS rm ON r.id = rm.role_id
                                WHERE (r.role_name = 'GovernanceAdmin' OR r.role_name = 'ClusterAdmin')
                                  AND (rm.member_sid = @apprSid OR (@itsmAccount IS NOT NULL AND LOWER(rm.member_sid) = LOWER(@itsmAccount)))";
        roleCmd.AddBound("@apprSid", approverSid.Value);
        roleCmd.AddBound("@itsmAccount", (object?)itsmApproverAccount ?? DBNull.Value, DbType.String);

        var roleAdminCount = Convert.ToInt64(await ExecuteScalarAsync(roleCmd, "ConsentGrants.CheckRoleAdmin", ct).ConfigureAwait(false));
        return roleAdminCount > 0;
    }
}

internal readonly record struct RevokeTarget(TableIdentifier Table, string? TenantRaw, bool IsGranteeSelf, bool AlreadyRevoked);

internal static class ConsentGrantSqlCore
{
    public const string SelectValidTo = "SELECT valid_to FROM CONSENTS WHERE id = @id AND tenant_id = @t AND is_revoked = 0";
    public const string UpdateDescription = "UPDATE CONSENTS SET description = @d WHERE id = @id AND tenant_id = @t AND is_revoked = 0";
    public const string RevokedDecision = "REVOKED";
    public const string MarkRevoked = @"UPDATE CONSENTS SET is_revoked = 1, revoked_by_sid = @by, revoked_at = @now, revoke_reason = @reason
                                WHERE id = @id AND is_revoked = 0";

    public static async Task<List<Consent>> ReadActiveRoleConsentsAsync(
        DbConnection conn, string sql, TableIdentifier table, string roleName, TenantId? tenantId, DateTimeOffset atTime, CancellationToken ct)
    {
        var consents = new List<Consent>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@domain", table.Domain);
        cmd.AddBound("@schema", table.Schema);
        cmd.AddBound("@table", table.TableName);
        if (tenantId != null)
        {
            cmd.AddBound("@tenantId", tenantId.Value.Value);
        }
        cmd.AddBound("@role", roleName);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var validFrom = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture);
            var validTo = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture);
            if (atTime < validFrom || atTime >= validTo) continue;

            consents.Add(new Consent
            {
                Id = Guid.Parse(reader.GetString(0)),
                TableId = Guid.Parse(reader.GetString(1)),
                TableIdentifier = table,
                ConsentRequestId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                Effect = Enum.Parse<ConsentEffect>(reader.GetString(3), true),
                GranteeType = GranteeType.Role,
                GranteeSid = reader.IsDBNull(5) ? (Sid?)null : new Sid(reader.GetString(5)),
                RoleId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
                RoleName = reader.IsDBNull(7) ? null : reader.GetString(7),
                ValidFrom = validFrom,
                ValidTo = validTo,
                IsRevoked = reader.GetInt32(10) == 1,
                TenantId = StoredTenant.Parse(reader.IsDBNull(11) ? null : reader.GetString(11))
            });
        }
        return consents;
    }

    public static async Task<RevokeTarget?> ReadRevokeTargetAsync(
        DbConnection conn, DbTransaction tx, string sql, Guid consentId, Sid actor, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.AddBound("@id", consentId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var table = new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2));
        var rawTenant = reader.IsDBNull(3) ? null : reader.GetString(3);
        var isGranteeSelf = string.Equals(reader.GetString(4), "User", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(reader.IsDBNull(5) ? null : reader.GetString(5), actor.Value, StringComparison.OrdinalIgnoreCase);
        var isRevoked = reader.GetInt32(6) == 1;

        return new RevokeTarget(table, rawTenant, isGranteeSelf, isRevoked);
    }

    public static async Task<bool> UpdateDescriptionAsync(
        DbConnection conn, DbTransaction tx, ConsentDescriptionUpdate update, TimeProvider clock, CancellationToken ct)
    {
        await using (var selCmd = conn.CreateCommand())
        {
            selCmd.Transaction = tx;
            selCmd.CommandText = SelectValidTo;
            selCmd.AddBound("@id", update.ConsentId.ToString());
            selCmd.AddBound("@t", update.Tenant.Value);

            var validToObj = await selCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (validToObj is null or DBNull)
            {
                return false;
            }

            if (DateTimeOffset.TryParse(Convert.ToString(validToObj, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var validTo)
                && validTo <= clock.GetUtcNow())
            {
                return false;
            }
        }

        await using var updCmd = conn.CreateCommand();
        updCmd.Transaction = tx;
        updCmd.CommandText = UpdateDescription;
        updCmd.AddBound("@d", (object?)update.Description ?? DBNull.Value);
        updCmd.AddBound("@id", update.ConsentId.ToString());
        updCmd.AddBound("@t", update.Tenant.Value);
        return await updCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public static async Task MarkRevokedAsync(
        DbConnection conn, DbTransaction tx, Guid consentId, Sid actor, string reason, TimeProvider clock, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = MarkRevoked;
        cmd.AddBound("@id", consentId.ToString());
        cmd.AddBound("@by", actor.Value);
        cmd.AddBound("@now", (clock ?? TimeProvider.System).GetUtcNow().ToString("O"));
        cmd.AddBound("@reason", reason);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task<bool> MarkRevokedAsync(
        DbConnection conn, DbTransaction tx, Guid consentId, Sid actor, string reason, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = MarkRevoked;
        cmd.AddBound("@id", consentId.ToString());
        cmd.AddBound("@by", actor.Value);
        cmd.AddBound("@now", now.ToString("O"));
        cmd.AddBound("@reason", reason);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    public static TenantId ResolveConsentTenant(string? raw, Guid consentId, string? targetTable)
    {
        if (TenantId.TryParse(raw, out var tenant))
        {
            return tenant;
        }

        throw new ConsentTenantIntegrityException(consentId, targetTable);
    }
}
