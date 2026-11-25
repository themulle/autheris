namespace Autheris.Infrastructure.Persistence;

using System.Data.Common;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
public partial class GovernanceRepository
{
    public async Task DeleteConsentRequestAsync(Guid requestId, CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await ConsentSqlCore.DeleteConsentRequestRowsAsync(conn, tx, requestId, ct).ConfigureAwait(false);
            await CommitTransactionAsync(tx, ct).ConfigureAwait(false);
        }
        catch
        {
            await RollbackTransactionAsync(tx, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task HydrateConsentsBatchAsync(DbConnection conn, List<Consent> consents, CancellationToken ct)
    {
        await ConsentSqlCore.HydrateConsentsBatchAsync(conn, consents, ct).ConfigureAwait(false);
        await ConsentPatternSql.HydrateApprovingOwnersBatchAsync(conn, consents, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Owner check of the repository-level request decisions: <paramref name="approverSid"/> must be exactly the owner group of the domain
    /// the table is explicitly assigned to in <paramref name="tenant"/>. Group membership is not expanded here and the Unassigned domain never
    /// qualifies, so this is never broader than <c>IContainerOwnership</c> (fail closed).
    /// </summary>
    private async Task<bool> IsContainerOwnerGroupInternalAsync(TenantId tenant, TableIdentifier table, Sid approverSid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.Value) || TenantId.IsReserved(tenant) || string.IsNullOrWhiteSpace(approverSid.Value))
        {
            return false;
        }

        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ContainerOwnerGroupForTableSql;
        cmd.AddBound("@tenantId", tenant.Value);
        cmd.AddBound("@unassignedId", Guid.Empty.ToString("D"));
        cmd.AddBound("@domain", table.Domain);
        cmd.AddBound("@schema", table.Schema);
        cmd.AddBound("@table", table.TableName);
        cmd.AddBound("@apprSid", approverSid.Value.Trim());
        var count = Convert.ToInt64(await ExecuteScalarAsync(cmd, "ConsentSupport.CheckContainerOwnerGroup", ct).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        return count > 0;
    }

    private const string ContainerOwnerGroupForTableSql = @"SELECT COUNT(*)
FROM TABLES t
INNER JOIN GOVERNANCE_DOMAIN_TABLES dt ON dt.table_id = t.id AND dt.tenant_id = @tenantId
INNER JOIN GOVERNANCE_DOMAINS d ON d.tenant_id = dt.tenant_id AND d.id = dt.domain_id
WHERE LOWER(t.source_name) = LOWER(@domain) AND LOWER(t.schema_name) = LOWER(@schema) AND LOWER(t.table_name) = LOWER(@table)
  AND t.is_active = 1
  AND d.id <> @unassignedId
  AND d.owner_group_sid IS NOT NULL
  AND UPPER(d.owner_group_sid) = UPPER(@apprSid)";
}
