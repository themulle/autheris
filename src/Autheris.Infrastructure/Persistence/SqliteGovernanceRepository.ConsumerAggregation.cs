namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public partial class SqliteGovernanceRepository : IConsumerAggregationRepository
{
    public Task RecordDailyUsageAsync(
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
        CancellationToken ct = default) =>
        WriteAsync(async conn =>
        {
            await ConsumerAggregationStore.RecordDailyUsageAsync(
                conn, ConsumerAggregationDialect.Sqlite, null, tableId, tenantId, day, application, clientId, version,
                calls, rowsReturned, rowsMeasuredCalls, rejAccess, rejRateLimit, rejCost, rejOther, ct).ConfigureAwait(false);
            return true;
        }, ct);

    public Task RecordActorSampleAsync(
        TableIdentifier tableId,
        TenantId tenantId,
        string day,
        string application,
        string actorHash,
        CancellationToken ct = default) =>
        WriteAsync(async conn =>
        {
            await ConsumerAggregationStore.RecordActorSampleAsync(
                conn, ConsumerAggregationDialect.Sqlite, null, tableId, tenantId, day, application, actorHash, ct).ConfigureAwait(false);
            return true;
        }, ct);

    public Task RecordQueryNameAsync(
        TableIdentifier tableId,
        TenantId tenantId,
        string day,
        string application,
        string queryName,
        long calls,
        CancellationToken ct = default) =>
        WriteAsync(async conn =>
        {
            await ConsumerAggregationStore.RecordQueryNameAsync(
                conn, ConsumerAggregationDialect.Sqlite, null, tableId, tenantId, day, application, queryName, calls, ct).ConfigureAwait(false);
            return true;
        }, ct);

    public Task<IReadOnlyList<ConsumerAppDailySummary>> QueryConsumerAppDailyAsync(
        TableIdentifier tableId,
        TenantId tenantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default) =>
        ReadAsync(conn => ConsumerAggregationStore.QueryConsumerAppDailyAsync(
            conn, null, tableId, tenantId, fromUtc, toUtc, ct), ct);

    public Task<IReadOnlyDictionary<string, int>> QueryDistinctActorCountsAsync(
        TableIdentifier tableId,
        TenantId tenantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default) =>
        ReadAsync(conn => ConsumerAggregationStore.QueryDistinctActorCountsAsync(
            conn, null, tableId, tenantId, fromUtc, toUtc, ct), ct);

    public Task<int> PurgeExpiredConsumerMetricsAsync(
        int retentionDays,
        DateTimeOffset asOfUtc,
        CancellationToken ct = default) =>
        WriteAsync(conn => ConsumerAggregationStore.PurgeExpiredConsumerMetricsAsync(
            conn, null, retentionDays, asOfUtc, ct), ct);
}
