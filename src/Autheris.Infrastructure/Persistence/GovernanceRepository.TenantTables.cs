namespace Autheris.Infrastructure.Persistence;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

public partial class GovernanceRepository
{
    /// <summary>
    /// SR-L-21: Tables on which <paramref name="tenantId"/> holds at least one non-revoked consent record (Allow or Deny,
    /// regardless of validity window or grantee). Used as the consent-derived tenant ownership signal for catalog isolation.
    /// </summary>
    public async Task<IReadOnlyList<TableIdentifier>> GetTablesWithConsentsForTenantAsync(TenantId tenantId, CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.Consents));
        const string sql = @"SELECT DISTINCT t.source_name, t.schema_name, t.table_name
                             FROM CONSENTS c
                             JOIN TABLES t ON c.table_id = t.id
                             WHERE c.is_revoked = 0 AND c.tenant_id = @tenantId";
        var result = new List<TableIdentifier>();
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@tenantId", tenantId.Value);
        await using var reader = await ExecuteReaderAsync(cmd, "TenantTables.GetTablesWithConsents", ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return result;
    }
}
