using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence.Dialects;

namespace Autheris.Infrastructure.Persistence;

public partial class GovernanceRepository
{
    /// <summary>
    /// Issue #1043: the per-table subject read (<see cref="GetActiveConsentsForSubjectsAsync"/>) for several tables. The direct consents are
    /// read with the per-table statement minus its table predicate and filtered with the same row filter, each built with the requested
    /// identifier of its table; the pattern consents are read once per domain; activity rule and hydration are those of the per-table read.
    /// </summary>
    public async Task<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<Consent>>> GetActiveConsentsForSubjectsBatchAsync(
        IEnumerable<Sid> subjects, IReadOnlyCollection<TableIdentifier> tables, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(tables);
        var tenant = TenantScope.RequireTenant(tenantId, nameof(GovernanceTable.Consents));
        var subjectEnumerated = subjects.ToList();
        var subjectSet = subjectEnumerated.ToHashSet();

        // TableIdentifier equality is case-insensitive, like the LOWER() match of the per-table read; the first spelling is kept.
        var lists = new Dictionary<TableIdentifier, List<Consent>>();
        var spelling = new Dictionary<TableIdentifier, TableIdentifier>();
        foreach (var table in tables)
        {
            if (lists.TryAdd(table, [])) spelling[table] = table;
        }

        if (lists.Count > 0 && subjectSet.Count > 0)
        {
            await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
            var seen = new HashSet<Guid>();
            foreach (var chunk in ConsentSubjectFilter.Chunks(ConsentSubjectFilter.DistinctNormalized(subjectEnumerated)))
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT c.id, c.table_id, c.consent_request_id, c.effect, c.grantee_type, c.grantee_sid, c.valid_from, c.valid_to,
                                       c.is_revoked, t.source_name, t.schema_name, t.table_name, c.tenant_id, " + ConsentWorkflowSql.ConsentColumns + @"
                                FROM CONSENTS c JOIN TABLES t ON c.table_id = t.id
                                WHERE c.is_revoked = 0 AND c.tenant_id = @tenantId" + ConsentSubjectFilter.InListPredicate(chunk.Count);
                for (var si = 0; si < chunk.Count; si++) cmd.AddBound("@s" + si, chunk[si]);
                cmd.AddBound("@tenantId", tenant.Value);
                await using var reader = await ExecuteReaderAsync(cmd, "Consent.GetActiveForSubjectsBatch", ct).ConfigureAwait(false);
                ConsentSqlCore.ReadSubjectConsentRows(
                    reader,
                    r => spelling.TryGetValue(new TableIdentifier(r.GetString(9), r.GetString(10), r.GetString(11)), out var requested) ? requested : null,
                    atTime, subjectSet, seen, (table, consent) => lists[table].Add(consent));
            }

            // Pattern consents of each table's domain after its direct consents, as the per-table read appends them (one read per domain).
            var patternsByDomain = new Dictionary<string, List<Consent>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (table, list) in lists)
            {
                var domainKey = table.Domain?.Trim() ?? string.Empty;
                if (!patternsByDomain.TryGetValue(domainKey, out var patterns))
                {
                    patternsByDomain[domainKey] = patterns = [];
                    await ConsentPatternSql.ReadActivePatternConsentsForDomainAsync(conn, table.Domain, tenant, atTime, subjectSet, [], patterns, ct).ConfigureAwait(false);
                }

                var present = list.Select(static c => c.Id).ToHashSet();
                list.AddRange(patterns.Where(p => present.Add(p.Id)));
                list.RemoveAll(c => !c.IsActive(atTime) && !(c.Effect == ConsentEffect.Deny && c.ValidFrom > atTime));
            }

            await HydrateConsentsBatchAsync(conn, lists.Values.SelectMany(static l => l).DistinctBy(static c => c.Id).ToList(), ct).ConfigureAwait(false);
        }

        return lists.ToDictionary(static kv => kv.Key, static kv => (IReadOnlyList<Consent>)kv.Value);
    }
}
