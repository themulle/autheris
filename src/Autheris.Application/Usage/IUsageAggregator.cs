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
        IReadOnlyList<UsageFact> facts,
        string? problemCode = null);

    void RecordUnscoped(
        TenantId tenant,
        Sid actor,
        in UsageFact fact);

    UsageGenerationSnapshot Swap();

    /// <summary>Merges a previously swapped generation back into the current generation after a transient flush failure.</summary>
    void Merge(UsageGenerationSnapshot snapshot);

    /// <summary>Current approximate count of queries awaiting late source-cost collection.</summary>
    int PendingSourceQueryCount { get; }

    /// <summary>Accumulates delayed source compute and cost measurements onto an existing usage series without altering request/query/outcome counts.</summary>
    void AggregateSourceStats(
        in UsageSeriesKey key,
        IReadOnlyList<string>? grantIds,
        long? sourceComputeMs,
        long? sourceCostMicros);

    /// <summary>Enqueues a pending source query for asynchronous collection if capacity permits.</summary>
    bool TryEnqueuePendingSourceQuery(PendingSourceQuery query);

    /// <summary>Dequeues up to <paramref name="maxCount"/> pending source queries for processing.</summary>
    IReadOnlyList<PendingSourceQuery> DrainPendingSourceQueries(int maxCount);

    /// <summary>Validates that <paramref name="sourceCostUnit"/> matches the expected unit configured for <paramref name="dataSource"/>.</summary>
    bool IsSourceCostUnitValid(string dataSource, string? sourceCostUnit);

    /// <summary>Copies unflushed series and group-set entries for <paramref name="tenant"/> within the UTC window for quota evaluation.</summary>
    void CollectQuotaUsage(
        TenantId tenant,
        DateOnly fromUtc,
        DateOnly toUtc,
        ICollection<UsageQuotaSeriesEntry> destination,
        IDictionary<string, IReadOnlyList<Sid>> groupSets);
}
