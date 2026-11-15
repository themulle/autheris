namespace Autheris.Application.Common.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>
/// Query-name aggregation and retention purge contract for <c>CONSUMER_APP_QUERYNAME_DAILY</c>.
/// </summary>
public interface IConsumerAggregationRepository
{
    Task RecordQueryNameAsync(
        TableIdentifier tableId,
        TenantId tenantId,
        string day,
        string application,
        string queryName,
        long calls,
        CancellationToken ct = default);

    Task<int> PurgeExpiredConsumerMetricsAsync(
        int retentionDays,
        DateTimeOffset asOfUtc,
        CancellationToken ct = default);
}
