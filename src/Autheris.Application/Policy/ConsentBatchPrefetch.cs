namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Issue #1043: the caller's consents for a set of tables (and the tenant's pattern consents), read once at <see cref="ReadAt"/> for a batch
/// of access decisions (<c>IAccessDecisionPoint.DecideManyAsync</c>) instead of once or twice per decision.
/// <para>
/// A decision at time <c>now</c> gets exactly the rows the per-table reads would return at <c>now</c>, restricted to what existed at
/// <see cref="ReadAt"/>: the row filter of the per-table read (subject, <c>now &lt; valid_to</c>, <c>now &gt;= valid_from</c> unless deny) and its
/// final activity rule are applied again at <c>now</c>. A consent that only became valid, or was created, after <see cref="ReadAt"/> is
/// therefore missing (fail closed: no grant); an expired one is dropped. A subject outside the prefetched set, another tenant, a table
/// that was not prefetched or a time before <see cref="ReadAt"/> is not answered (the caller reads from the repository as before).
/// It lives for one batch of decisions only.
/// </para>
/// </summary>
public sealed class ConsentBatchPrefetch
{
    private readonly HashSet<Sid> _subjects;
    private readonly IReadOnlyDictionary<TableIdentifier, IReadOnlyList<Consent>> _byTable;
    private readonly IReadOnlyList<Consent> _tenantPatterns;

    private ConsentBatchPrefetch(
        TenantId tenant,
        HashSet<Sid> subjects,
        DateTimeOffset readAt,
        IReadOnlyDictionary<TableIdentifier, IReadOnlyList<Consent>> byTable,
        IReadOnlyList<Consent> tenantPatterns)
    {
        Tenant = tenant;
        _subjects = subjects;
        ReadAt = readAt;
        _byTable = byTable;
        _tenantPatterns = tenantPatterns;
    }

    public TenantId Tenant { get; }

    public DateTimeOffset ReadAt { get; }

    /// <summary>Reads the consents of <paramref name="subjects"/> for <paramref name="tables"/> and the tenant's pattern consents at <paramref name="readAt"/>.</summary>
    public static async Task<ConsentBatchPrefetch> LoadAsync(
        IConsentRepository repository,
        IEnumerable<Sid> subjects,
        IReadOnlyCollection<TableIdentifier> tables,
        TenantId tenant,
        DateTimeOffset readAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(tables);
        var subjectSet = subjects.ToHashSet();
        var byTable = await repository.GetActiveConsentsForSubjectsBatchAsync(subjectSet, tables, readAt, tenant, ct).ConfigureAwait(false);
        var patterns = await repository.GetActivePatternConsentsAsync(tenant, readAt, ct).ConfigureAwait(false);
        return new ConsentBatchPrefetch(tenant, subjectSet, readAt, byTable, patterns);
    }

    /// <summary>
    /// The result of <see cref="IConsentRepository.GetActiveConsentsForSubjectsAsync"/> for <paramref name="subjects"/>, <paramref name="table"/>,
    /// <paramref name="now"/> and <paramref name="tenant"/>, when this prefetch covers the request.
    /// </summary>
    public bool TryGetActiveConsents(
        IEnumerable<Sid> subjects,
        TableIdentifier table,
        DateTimeOffset now,
        TenantId tenant,
        out List<Consent> consents)
    {
        consents = [];
        if (tenant != Tenant || now < ReadAt || !_byTable.TryGetValue(table, out var prefetched))
        {
            return false;
        }

        var callSubjects = subjects.ToHashSet();
        if (callSubjects.Count == 0)
        {
            return true; // the per-table read returns nothing without subjects
        }

        foreach (var subject in callSubjects)
        {
            if (!_subjects.Contains(subject))
            {
                return false;
            }
        }

        foreach (var consent in prefetched)
        {
            // Row filter of the per-table read, at the decision time.
            if (now >= consent.ValidTo) continue;
            if (now < consent.ValidFrom && consent.Effect != ConsentEffect.Deny) continue;
            if (consent.GranteeSid is not { } grantee || !callSubjects.Contains(grantee)) continue;

            // Final activity rule of the per-table read.
            if (!consent.IsActive(now) && !(consent.Effect == ConsentEffect.Deny && consent.ValidFrom > now)) continue;
            consents.Add(consent);
        }

        return true;
    }

    /// <summary>The result of <see cref="IConsentRepository.GetActivePatternConsentsAsync"/> for <paramref name="tenant"/> at <paramref name="now"/>.</summary>
    public bool TryGetActivePatternConsents(TenantId tenant, DateTimeOffset now, out List<Consent> consents)
    {
        consents = [];
        if (tenant != Tenant || now < ReadAt)
        {
            return false;
        }

        foreach (var consent in _tenantPatterns)
        {
            if (consent.IsActive(now))
            {
                consents.Add(consent);
            }
        }

        return true;
    }
}
