namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;

/// <summary>The consent row a revoke acts on, read under the dialect's row lock; the tenant is resolved from this locked row.</summary>
internal readonly record struct RevokeTarget(TableIdentifier Table, string? TenantRaw, bool IsGranteeSelf, bool AlreadyRevoked);

/// <summary>
/// Engine-neutral statement execution of the consent grant paths (ARCH-MNT-22 step 1, security-driven scope). Only statement-level pieces
/// live here: parameter binding, execution and row mapping. Authorisation (grantee-self / approver check), the <c>alreadyRevoked</c> decision,
/// the audit entry, transactions, rollback, epoch bump and cache eviction stay in the dialect repositories (SC-02, SC-03); this class gets an
/// open connection and transaction and never commits, rolls back, opens or disposes either. Tenant filter exemptions (SC-04): (a) revoke by
/// consent id under the row lock (<see cref="ReadRevokeTargetAsync"/> and <see cref="MarkRevokedAsync"/>; the tenant is read from the locked
/// row); every other statement here carries its tenant predicate (<c>c.tenant_id = @tenantId</c>, <c>tenant_id = @t AND is_revoked = 0</c>).
/// No secrets, reasons or payloads appear in any message of this class.
/// </summary>
internal static class ConsentGrantSqlCore
{
    // valid_to is stored as ISO text with the writer's offset, so the expiry is compared as a timestamp, not as text.
    public const string SelectValidTo = "SELECT valid_to FROM CONSENTS WHERE id = @id AND tenant_id = @t AND is_revoked = 0";

    public const string UpdateDescription = "UPDATE CONSENTS SET description = @d WHERE id = @id AND tenant_id = @t AND is_revoked = 0";

    /// <summary>Audit decision of every consent revoke path in every dialect (DEF-03, DEF-SC-07). A <c>const</c>, so the literal stays in the IL and in the goldens.</summary>
    public const string RevokedDecision = "REVOKED";

    public const string MarkRevoked = @"UPDATE CONSENTS SET is_revoked = 1, revoked_by_sid = @by, revoked_at = @now, revoke_reason = @reason
                                WHERE id = @id AND is_revoked = 0";

    /// <summary>Reads the active role consents of <paramref name="table"/>; <paramref name="sql"/> carries the tenant predicate iff <paramref name="tenantId"/> is set.</summary>
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
                ValidFrom = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                ValidTo = DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture),
                IsRevoked = reader.GetInt32(10) == 1,
                TenantId = StoredTenant.Parse(reader.IsDBNull(11) ? null : reader.GetString(11))
            };
            if (consent.IsActive(atTime))
            {
                consents.Add(consent);
            }
        }

        return consents;
    }

    /// <summary>
    /// Sets <c>CONSENTS.description</c> of one non-revoked, unexpired consent of the tenant inside <paramref name="tx"/>; touches no other column.
    /// Returns false (no row changed) for an unknown, foreign-tenant, revoked or expired consent (SEC-GOV158I1-05).
    /// </summary>
    public static async Task<bool> UpdateDescriptionAsync(DbConnection conn, DbTransaction tx, ConsentDescriptionUpdate update, TimeProvider clock, CancellationToken ct)
    {
        string? validTo;
        await using (var sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = SelectValidTo;
            sel.AddBound("@id", update.ConsentId.ToString());
            sel.AddBound("@t", update.Tenant.Value);
            validTo = await sel.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }

        if (validTo is null || DateTimeOffset.Parse(validTo, CultureInfo.InvariantCulture) <= clock.GetUtcNow())
        {
            return false;
        }

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = UpdateDescription;
        cmd.AddBound("@d", (object?)update.Description ?? DBNull.Value);
        cmd.AddBound("@id", update.ConsentId.ToString());
        cmd.AddBound("@t", update.Tenant.Value);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <summary>Reads the revoke target by consent id inside <paramref name="tx"/> (the dialect SQL takes the row lock); null when the id does not exist. Tenant filter exemption (SC-04 a).</summary>
    public static async Task<RevokeTarget?> ReadRevokeTargetAsync(DbConnection conn, DbTransaction tx, string sql, Guid consentId, Sid actor, CancellationToken ct)
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
        var tenantRaw = reader.IsDBNull(3) ? null : reader.GetString(3);
        var isGranteeSelf = string.Equals(reader.GetString(4), "User", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(reader.IsDBNull(5) ? null : reader.GetString(5), actor.Value, StringComparison.OrdinalIgnoreCase);
        var alreadyRevoked = reader.GetInt32(6) == 1;
        return new RevokeTarget(table, tenantRaw, isGranteeSelf, alreadyRevoked);
    }

    /// <summary>Marks the consent revoked inside <paramref name="tx"/>; the caller has authorised the actor and checked <c>alreadyRevoked</c>. Tenant filter exemption (SC-04 a).</summary>
    public static async Task MarkRevokedAsync(DbConnection conn, DbTransaction tx, Guid consentId, Sid actor, string reason, TimeProvider clock, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = MarkRevoked;
        cmd.AddBound("@id", consentId.ToString());
        cmd.AddBound("@by", actor.Value);
        cmd.AddBound("@now", clock.GetUtcNow().ToString("O"));
        cmd.AddBound("@reason", reason);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The only tenant parse of the revoke and extend paths (DEF-SC-01): NULL, empty, whitespace, over-length and regex-invalid values throw
    /// the governed <see cref="ConsentTenantIntegrityException"/> (fixed message, no raw value, no inner exception). Never defaults.
    /// </summary>
    public static TenantId ResolveConsentTenant(string? raw, Guid consentId, string? targetTable)
    {
        if (TenantId.TryParse(raw, out var tenant))
        {
            return tenant;
        }

        throw new ConsentTenantIntegrityException(consentId, targetTable);
    }
}
