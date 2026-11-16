namespace Autheris.Infrastructure.Persistence;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Infrastructure.Persistence.Dialects;

public partial class GovernanceRepository : IConsumerAggregationRepository
{
    private ConsumerAggregationDialect ConsumerDialect =>
        _dialect is PostgreSqlGovernanceDialect
            ? ConsumerAggregationDialect.PostgreSql
            : ConsumerAggregationDialect.SqlServer;

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
            conn, ConsumerDialect, null, tableId, tenantId, day, application, queryName, calls, ct).ConfigureAwait(false);
    }

    // QueryConsumerAppDailyAsync and QueryDistinctActorCountsAsync moved to GovernanceRepository.Usage.cs (over USAGE_DAILY).

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
