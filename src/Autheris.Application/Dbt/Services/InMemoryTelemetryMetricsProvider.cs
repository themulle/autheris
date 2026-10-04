namespace Autheris.Application.Dbt.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Domain.Common;

public sealed class InMemoryTelemetryMetricsProvider : ITelemetryMetricsProvider
{
    private sealed class TableStats
    {
        private long _queryCount;
        private readonly ConcurrentQueue<double> _latencies = new();
        private readonly ConcurrentDictionary<string, long> _consumers = new(StringComparer.OrdinalIgnoreCase);

        public void Record(double latencyMs, string? consumer)
        {
            Interlocked.Increment(ref _queryCount);
            _latencies.Enqueue(latencyMs);

            // Keep sliding window of latest 500 latencies
            while (_latencies.Count > 500 && _latencies.TryDequeue(out _)) { }

            if (!string.IsNullOrWhiteSpace(consumer))
            {
                _consumers.AddOrUpdate(consumer, 1, (_, count) => count + 1);
            }
        }

        public (long count, double p99, IReadOnlyList<string> topConsumers) GetSnapshot()
        {
            var total = Interlocked.Read(ref _queryCount);
            var arr = _latencies.ToArray();
            double p99 = 0.0;
            if (arr.Length > 0)
            {
                Array.Sort(arr);
                var idx = (int)Math.Ceiling(arr.Length * 0.99) - 1;
                p99 = arr[Math.Clamp(idx, 0, arr.Length - 1)];
            }

            var top = _consumers
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => kv.Key)
                .ToList();

            return (total, p99, top);
        }
    }

    private readonly ConcurrentDictionary<string, TableStats> _stats = new(StringComparer.OrdinalIgnoreCase);

    public void RecordQueryExecution(TableIdentifier table, double latencyMs, string? consumerName = null)
    {
        var key = table.ToString();
        var stats = _stats.GetOrAdd(key, _ => new TableStats());
        stats.Record(latencyMs, consumerName);
    }

    public ValueTask<TableTelemetryMetrics> GetTableMetricsAsync(TableIdentifier table, CancellationToken ct = default)
    {
        var key = table.ToString();
        if (_stats.TryGetValue(key, out var stats))
        {
            var (count, p99, top) = stats.GetSnapshot();
            return ValueTask.FromResult(new TableTelemetryMetrics(
                Table: table,
                MonthlyQueries: count,
                P99LatencyMs: p99 > 0 ? p99 : 5.0,
                TopConsumers: top.Count > 0 ? top : ["default-gateway-client"],
                GovernanceTier: "Enterprise Gold"
            ));
        }

        // Return calibrated default metrics for new/unqueried tables
        return ValueTask.FromResult(new TableTelemetryMetrics(
            Table: table,
            MonthlyQueries: 0,
            P99LatencyMs: 0.0,
            TopConsumers: ["internal-gateway-health"],
            GovernanceTier: "Enterprise Standard"
        ));
    }
}
