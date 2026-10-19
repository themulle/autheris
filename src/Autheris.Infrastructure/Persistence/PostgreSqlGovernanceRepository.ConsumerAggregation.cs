namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public partial class PostgreSqlGovernanceRepository : IConsumerAggregationRepository
{
    public async Task RecordDailyUsageAsync(
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
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await ConsumerAggregationStore.RecordDailyUsageAsync(
            conn, null, tableId, tenantId, day, application, clientId, version,
            calls, rowsReturned, rowsMeasuredCalls, rejAccess, rejRateLimit, rejCost, rejOther, ct).ConfigureAwait(false);
    }

    public async Task RecordActorSampleAsync(
        TableIdentifier tableId,
        TenantId tenantId,
        string day,
        string application,
        string actorHash,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await ConsumerAggregationStore.RecordActorSampleAsync(
            conn, null, tableId, tenantId, day, application, actorHash, ct).ConfigureAwait(false);
    }

    public async Task RecordQueryNameAsync(
        TableIdentifier tableId,
        TenantId tenantId,
        string day,
        string application,
        string queryName,
        long calls,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        await ConsumerAggregationStore.RecordQueryNameAsync(
            conn, null, tableId, tenantId, day, application, queryName, calls, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConsumerAppDailySummary>> QueryConsumerAppDailyAsync(
        TableIdentifier tableId,
        TenantId tenantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ConsumerAggregationStore.QueryConsumerAppDailyAsync(
            conn, null, tableId, tenantId, fromUtc, toUtc, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, int>> QueryDistinctActorCountsAsync(
        TableIdentifier tableId,
        TenantId tenantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ConsumerAggregationStore.QueryDistinctActorCountsAsync(
            conn, null, tableId, tenantId, fromUtc, toUtc, ct).ConfigureAwait(false);
    }

    public async Task<int> PurgeExpiredConsumerMetricsAsync(
        int retentionDays,
        DateTimeOffset asOfUtc,
        CancellationToken ct = default)
    {
        await using var conn = await OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ConsumerAggregationStore.PurgeExpiredConsumerMetricsAsync(
            conn, null, retentionDays, asOfUtc, ct).ConfigureAwait(false);
    }
}
