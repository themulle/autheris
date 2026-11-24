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
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Grants;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

/// <summary>System revocation by id and the kind-complete grant enumeration used by deprovisioning and grant renewal.</summary>
public partial class GovernanceRepository
{
    /// <summary>All grant kinds; with <paramref name="maxResults"/> &gt; 0 only Allow rows are read and the SQL read is bounded at that count.</summary>
    public async Task<IReadOnlyList<Consent>> GetActiveGrantsForSubjectsAsync(
        TenantId tenantId, IReadOnlyCollection<Sid> subjects, DateTimeOffset atTime, int maxResults, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        var all = await AllActiveCoreAsync(subjects, atTime, TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.Consents)), false, maxResults, ct).ConfigureAwait(false);
        return maxResults > 0 && all.Count > maxResults ? all.Take(maxResults).ToList() : all;
    }

    public async Task<bool> RevokeSystemConsentByIdAsync(TenantId tenantId, Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default)
    {
        var tenant = TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.Consents));
        StoredTenant.RequireWritable(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(revokedBySid.Value);
        var grantWriter = RequireGrantWriter();
        TableIdentifier? invalidateTable = null;
        var publishCatalog = false;
        Autheris.Application.Catalog.CatalogVersion newCatalogVersion = default;

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var target = await ConsentGrantSqlCore.ReadRevokeTargetAsync(conn, tx, RevokeTargetSql, consentId, revokedBySid, ct).ConfigureAwait(false);
            if (target is not { } found
                || found.AlreadyRevoked
                || !TenantId.TryParse(found.TenantRaw, out var rowTenant)
                || rowTenant != tenant)
            {
                await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
                return false;
            }

            var isPattern = !string.IsNullOrWhiteSpace(found.ObjectPattern);
            var scopeLabel = isPattern ? found.ObjectPattern! : found.Table.ToString();
            var now = TimeProvider.System.GetUtcNow();
            if (!await MarkRevokedForTenantAsync(conn, tx, consentId, tenant, revokedBySid, reason, now, ct).ConfigureAwait(false))
            {
                await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
                return false;
            }

            await ConsentGrantSqlCore.RecordRevokedGrantAsync(grantWriter, tx, consentId, found.Table, tenant, found, revokedBySid, reason, now, ct).ConfigureAwait(false);
            await RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = tenant,
                EventType = AuditEventTypes.ConsentRevoked,
                ActorSid = revokedBySid,
                TargetTable = scopeLabel,
                Decision = ConsentGrantSqlCore.RevokedDecision,
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new { ConsentId = consentId, RevokedBy = revokedBySid.Value, Reason = reason, Scope = scopeLabel, RevokedAt = now })
            }, tx, ct).ConfigureAwait(false);

            if (isPattern)
            {
                newCatalogVersion = await BumpCatalogVersionInternalAsync(conn, tx, 0, 1, ct).ConfigureAwait(false);
                publishCatalog = true;
            }
            else if (!string.IsNullOrWhiteSpace(found.Table.TableName))
            {
                await IncrementTableEpochInternalAsync(conn, tx, found.Table, ct).ConfigureAwait(false);
                invalidateTable = found.Table;
            }

            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }

        if (publishCatalog)
        {
            await PublishCatalogChangedEventAsync(newCatalogVersion, ct).ConfigureAwait(false);
        }
        else if (invalidateTable is { } table)
        {
            await InvalidateTableEpochAsync(table, ct).ConfigureAwait(false);
        }

        return true;
    }

    private const string MarkRevokedForTenant = @"UPDATE CONSENTS SET is_revoked = 1, revoked_by_sid = @by, revoked_at = @now, revoke_reason = @reason
                                WHERE id = @id AND tenant_id = @t AND is_revoked = 0";

    private static async Task<bool> MarkRevokedForTenantAsync(
        DbConnection conn, DbTransaction tx, Guid consentId, TenantId tenant, Sid actor, string reason, DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = MarkRevokedForTenant;
        cmd.AddBound("@id", consentId.ToString());
        cmd.AddBound("@t", tenant.Value);
        cmd.AddBound("@by", actor.Value);
        cmd.AddBound("@now", now.ToString("O"));
        cmd.AddBound("@reason", reason);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }
}
