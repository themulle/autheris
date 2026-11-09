namespace Autheris.Application.Usage;

using System.Collections.Generic;
using Autheris.Domain.Common;

public sealed class UsageGenerationSnapshot
{
    public UsageGenerationSnapshot(
        IReadOnlyDictionary<UsageSeriesKey, UsageSeriesCounters> series,
        IReadOnlyDictionary<UsageGrantSeriesKey, UsageGrantSeriesCounters> grantSeries,
        IReadOnlyDictionary<(TenantId Tenant, string GroupSetId), IReadOnlyList<Sid>> groupSets,
        IReadOnlyDictionary<(TenantId Tenant, string GrantId), GrantRef> grantRefs)
    {
        Series = series;
        GrantSeries = grantSeries;
        GroupSets = groupSets;
        GrantRefs = grantRefs;
    }

    public IReadOnlyDictionary<UsageSeriesKey, UsageSeriesCounters> Series { get; }

    public IReadOnlyDictionary<UsageGrantSeriesKey, UsageGrantSeriesCounters> GrantSeries { get; }

    public IReadOnlyDictionary<(TenantId Tenant, string GroupSetId), IReadOnlyList<Sid>> GroupSets { get; }

    public IReadOnlyDictionary<(TenantId Tenant, string GrantId), GrantRef> GrantRefs { get; }
}
