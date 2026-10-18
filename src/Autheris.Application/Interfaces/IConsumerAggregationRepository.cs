namespace Autheris.Application.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Architecture Phase 2 / Issue #114: Repository contract for managing pre-aggregated daily consumer telemetry.
/// Maintains operational counters per table, normalized calling application, and UTC day.
/// </summary>
public interface IConsumerAggregationRepository
{
    Task RecordDailyUsageAsync(
        TableIdentifier tableId,
        TenantId tenantId,
        string day,
        string application,
        string clientId,
        string version,
        long calls,
        long rowsReturned,
        long rowsMeasuredCalls,
        long rejAccess,
        long rejRateLimit,
        long rejCost,
        long rejOther,
        CancellationToken ct = default);

    Task RecordActorSampleAsync(
        TableIdentifier tableId,
        TenantId tenantId,
        string day,
        string application,
        string actorHash,
        CancellationToken ct = default);

    Task RecordQueryNameAsync(
        TableIdentifier tableId,
        TenantId tenantId,
        string day,
        string application,
        string queryName,
        long calls,
        CancellationToken ct = default);

    Task<IReadOnlyList<ConsumerAppDailySummary>> QueryConsumerAppDailyAsync(
        TableIdentifier tableId,
        TenantId tenantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, int>> QueryDistinctActorCountsAsync(
        TableIdentifier tableId,
        TenantId tenantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default);

    Task<int> PurgeExpiredConsumerMetricsAsync(
        int retentionDays,
        DateTimeOffset asOfUtc,
        CancellationToken ct = default);
}
