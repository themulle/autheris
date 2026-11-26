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

/// <summary>System revocation by id (grant.revoked for deprovisioning, none for renewal) and the kind-complete grant enumeration used by deprovisioning and grant renewal.</summary>
public partial class GovernanceRepository
{
    private const string SelectPendingRequestsForSubjectSql =
        @"SELECT r.id, r.table_id, r.requester_sid, r.requested_grantee_type, r.requested_grantee_ref,
                 r.business_justification, r.status, r.requested_at, r.requested_valid_to,
                 t.source_name, t.schema_name, t.table_name, r.context_json, r.requested_write_rules_json,
                 r.version, r.workflow_mode, r.external_system_id, r.external_reference, r.requested_valid_from, r.updated_at, r.request_kind, r.profile_id, r.grant_kind, r.target_columns, r.container_kind, r.container_id, r.approver_capacity, r.self_grant, r.bundle_id, r.decided_by, r.decided_at,
                 r.tenant_id
          FROM CONSENT_REQUESTS r
          LEFT JOIN TABLES t ON r.table_id = t.id
          WHERE r.tenant_id = @tenantId
            AND UPPER(r.status) IN ('PENDING', 'PENDING_SECOND_APPROVAL', 'PENDING_EXTERNAL_APPROVAL')
            AND (r.requester_sid = @sub OR UPPER(r.requester_sid) = @subNorm OR r.requested_grantee_ref = @sub OR UPPER(r.requested_grantee_ref) = @subNorm)
          ORDER BY r.requested_at DESC";

    /// <summary>All grant kinds including scheduled future grants; with <paramref name="maxResults"/> &gt; 0 only Allow rows are read and bounded.</summary>
    public Task<IReadOnlyList<Consent>> GetActiveGrantsForSubjectsAsync(
        TenantId tenantId, IReadOnlyCollection<Sid> subjects, DateTimeOffset atTime, int maxResults, CancellationToken ct = default) =>
        GetActiveGrantsForSubjectsAfterAsync(tenantId, subjects, atTime, afterId: null, maxResults, ct);

    /// <summary>Keyset-paginated grant enumeration ordered by consent ID for deprovisioning sweeps.</summary>
    public async Task<IReadOnlyList<Consent>> GetActiveGrantsForSubjectsAfterAsync(
        TenantId tenantId, IReadOnlyCollection<Sid> subjects, DateTimeOffset atTime, Guid? afterId, int maxResults, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        var all = await AllActiveCoreAsync(subjects, atTime, TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.Consents)), false, maxResults, ct, includeFuture: true, afterId: afterId).ConfigureAwait(false);
        return maxResults > 0 && all.Count > maxResults ? all.Take(maxResults).ToList() : all;
    }

    public async Task<IReadOnlyList<ConsentRequest>> GetPendingRequestsForSubjectAsync(Sid subject, TenantId tenantId, CancellationToken ct = default)
    {
        var validTenant = TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.ConsentRequests));
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = SelectPendingRequestsForSubjectSql;
        cmd.AddBound("@tenantId", validTenant.Value);
        cmd.AddBound("@sub", subject.Value);
        cmd.AddBound("@subNorm", ConsentSubjectFilter.Normalize(subject.Value));

        var list = new List<ConsentRequest>();
        await using var reader = await ExecuteReaderAsync(cmd, "ConsentRequest.GetPendingForSubject", ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var req = ConsentSqlCore.ReadSummaryConsentRequestRow(reader);
            if (req.RequesterSid == subject ||
                (req.RequestedGranteeType != GranteeType.Group &&
                 !string.IsNullOrWhiteSpace(req.RequestedGranteeRef) &&
                 new Sid(req.RequestedGranteeRef.Trim()) == subject))
            {
                list.Add(req);
            }
        }

        return list;
    }

    public async Task<bool> RevokeSystemConsentByIdAsync(
        TenantId tenantId, Guid consentId, Sid revokedBySid, string reason, SystemRevocationKind kind = SystemRevocationKind.Revoked, CancellationToken ct = default)
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

            // Deprovisioning ends access like a person's revocation: grant.revoked commits with it, event lock last. A renewal
            // replaces the grant (or rolls a renewed one back) while access continues, so it emits no grant.revoked.
            if (kind == SystemRevocationKind.Revoked)
            {
                await EmitGrantRevokedEventsInTransactionAsync(conn, tx, [new PendingGrantRevokedEvent(consentId, tenant, found.Table, now)], ct)
                    .ConfigureAwait(false);
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
            await _writeInvalidator.InvalidateWideAsync(tenant, newCatalogVersion, ct: ct).ConfigureAwait(false);
        }
        else if (invalidateTable is { } table)
        {
            await _writeInvalidator.InvalidateTablesAsync(tenant, [table], metadataInvalidator: InvalidateTableMetadata, ct: ct).ConfigureAwait(false);
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
