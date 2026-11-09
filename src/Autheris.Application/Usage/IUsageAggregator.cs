namespace Autheris.Application.Usage;

using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;

public interface IUsageAggregator
{
    void Aggregate(
        in UsageSeriesKey key,
        long requests,
        long queries,
        long rows,
        bool rowsMeasured,
        long bytes,
        long durationMs,
        long analysisCost,
        long? sourceComputeMs,
        long? sourceCostMicros,
        UsageOutcome outcome);

    void AggregateGrant(
        in UsageGrantSeriesKey key,
        long accesses,
        long queries,
        long rows,
        long bytes,
        long analysisCost,
        long? sourceComputeMs,
        long? sourceCostMicros);

    void Record(
        UsageIdentity identity,
        DataSourceTransportFlags transport,
        CallerContext? caller,
        long durationMs,
        long bytes,
        int statusCode,
        IReadOnlyList<UsageFact> facts);

    void RecordUnscoped(
        TenantId tenant,
        Sid actor,
        in UsageFact fact);

    UsageGenerationSnapshot Swap();
}
