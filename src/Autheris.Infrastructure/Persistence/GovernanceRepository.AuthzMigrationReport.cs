namespace Autheris.Infrastructure.Persistence;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Interfaces;

public partial class GovernanceRepository
{
    public async Task<IReadOnlyList<RevokedRoleConsent>> GetRevokedRoleConsentsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, tenant_id, role_name, table_id, revoke_reason FROM CONSENTS WHERE grantee_type = 'Role' AND is_revoked = 1 AND revoke_reason = 'ADR020_ROLE_GRANTEE_UNSUPPORTED' ORDER BY tenant_id, id";
        var rows = new List<RevokedRoleConsent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new RevokedRoleConsent(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return rows;
    }

    public async Task<IReadOnlyList<DeactivatedProfileAssignment>> GetDeactivatedAssignmentsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tenant_id, profile_id, subject, subject_type, deactivated_reason FROM ACCESS_PROFILE_ASSIGNMENTS WHERE deactivated_reason IS NOT NULL ORDER BY tenant_id, profile_id, subject";
        var rows = new List<DeactivatedProfileAssignment>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new DeactivatedProfileAssignment(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return rows;
    }
}
