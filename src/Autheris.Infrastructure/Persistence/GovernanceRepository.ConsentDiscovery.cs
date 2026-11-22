namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

public partial class GovernanceRepository
{
    public async Task<IReadOnlySet<TableIdentifier>> GetRecentlyExpiredTableIdsAsync(
        Sid userSid, TenantId tenantId, DateTimeOffset since, CancellationToken ct = default)
    {
        TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.Consents));
        const string sql = @"SELECT DISTINCT t.source_name, t.schema_name, t.table_name, c.grantee_sid
                               FROM CONSENTS c
                               JOIN TABLES t ON c.table_id = t.id
                               WHERE c.tenant_id = @tenantId
                                 AND c.is_revoked = 0
                                 AND c.grant_kind = 'DATA'
                                 AND UPPER(c.effect) = 'ALLOW'
                                 AND (LOWER(c.grantee_sid) = LOWER(@sid) OR LOWER(COALESCE(c.grantee_sid_norm, '')) = LOWER(@sidNorm))
                                 AND c.valid_to >= @since
                                 AND c.valid_to < @now";
        var now = TimeProvider.System.GetUtcNow();
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddBound("@tenantId", tenantId.Value);
        cmd.AddBound("@sid", userSid.Value);
        cmd.AddBound("@sidNorm", ConsentSubjectFilter.NormalizeParameter(userSid.Value));
        cmd.AddBound("@since", since.ToUniversalTime().ToString("O"));
        cmd.AddBound("@now", now.ToUniversalTime().ToString("O"));

        var result = new HashSet<TableIdentifier>();
        await using var reader = await ExecuteReaderAsync(cmd, "Consent.GetRecentlyExpiredTableIds", ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.IsDBNull(3) || new Sid(reader.GetString(3)) != userSid)
            {
                continue;
            }

            result.Add(new TableIdentifier(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return result;
    }
}
