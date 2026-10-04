namespace Autheris.Application.Dbt.Interfaces;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

public sealed record TableTelemetryMetrics(
    TableIdentifier Table,
    long MonthlyQueries,
    double P99LatencyMs,
    IReadOnlyList<string> TopConsumers,
    string GovernanceTier = "Enterprise Gold"
);

public interface ITelemetryMetricsProvider
{
    ValueTask<TableTelemetryMetrics> GetTableMetricsAsync(TableIdentifier table, CancellationToken ct = default);
    void RecordQueryExecution(TableIdentifier table, double latencyMs, string? consumerName = null);
}
