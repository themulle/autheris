using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Infrastructure.Persistence.Dialects;

namespace Autheris.Infrastructure.Persistence;

/// <summary>Reads the earliest future Allow validity start across bounded subject chunks (#1063 §4d).</summary>
internal static class ScheduledAllowReader
{
    internal static async Task<DateTimeOffset?> ReadAsync(
        Func<CancellationToken, ValueTask<DbConnection>> openConnection, Func<DbConnection, string, DbCommand> createCommand,
        Func<DbCommand, string, CancellationToken, Task<DbDataReader>> executeReader,
        IEnumerable<Sid> subjects, DateTimeOffset atTime, TenantId tenantId, CancellationToken ct)
    {
        var tenant = TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.Consents));
        var subjectList = (subjects ?? throw new ArgumentNullException(nameof(subjects))).ToList();
        var subjectSet = subjectList.ToHashSet();
        if (subjectSet.Count == 0) return null;

        DateTimeOffset? earliest = null;
        await using var conn = await openConnection(ct).ConfigureAwait(false);
        foreach (var chunk in ConsentSubjectFilter.Chunks(ConsentSubjectFilter.DistinctNormalized(subjectList)))
        {
            var sql = @"SELECT c.grantee_type, c.grantee_sid, c.valid_from, c.valid_to FROM CONSENTS c WHERE c.is_revoked = 0 AND c.tenant_id = @tenantId AND c.grant_kind = 'DATA' AND UPPER(c.effect) = 'ALLOW'" + ConsentSubjectFilter.InListPredicate(chunk.Count);
            await using var cmd = createCommand(conn, sql);
            for (var si = 0; si < chunk.Count; si++) cmd.AddBound("@s" + si, chunk[si]);
            cmd.AddBound("@tenantId", tenant.Value);
            await using var reader = await executeReader(cmd, "Consent.GetNextScheduledAllow", ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.IsDBNull(1) || !subjectSet.Contains(new Sid(reader.GetString(1)))) continue;
                if (!DateTimeOffset.TryParse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var validFrom) ||
                    !DateTimeOffset.TryParse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var validTo)) continue;
                if (validFrom > atTime && validTo > validFrom && (earliest is null || validFrom < earliest.Value)) earliest = validFrom;
            }
        }
        return earliest;
    }
}
