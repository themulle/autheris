namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Usage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class SourceCostCollectorWorkerTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly Sid UserSid = new("S-1-5-21-100-200-300");
    private static readonly TableIdentifier TableId = new("sales", "dbo", "orders");

    [Fact]
    public async Task Collected_samples_update_source_compute_and_cost_without_incrementing_requests_or_grants()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        var aggregator = new UsageAggregator(timeProvider: time);
        var collector = new FakeCollector("Databricks", "DBU", (_, _) => Task.FromResult<IReadOnlyList<SourceCostSample>>(
            [new SourceCostSample("stmt-1", 420, 2.5m, "DBU")]));
        using var worker = CreateWorker(aggregator, [collector], time);

        aggregator.Record(
            new UsageIdentity(Tenant, UserSid, new HashSet<Sid>()),
            DataSourceTransportFlags.Rest,
            null,
            25,
            512,
            200,
            [new UsageFact(
                TableId,
                "dbx-main",
                UsageOutcome.Allow,
                10,
                true,
                3,
                [new GrantRef("g1", null, null)],
                new SourceExecutionStats(null, null, null, "stmt-1"))]);

        aggregator.PendingSourceQueryCount.ShouldBe(1);
        var initial = aggregator.Swap();
        initial.Series.Values.ShouldHaveSingleItem().Requests.ShouldBe(1);

        await worker.CollectOnceAsync(CancellationToken.None);

        aggregator.PendingSourceQueryCount.ShouldBe(0);
        var late = aggregator.Swap();
        var series = late.Series.Values.ShouldHaveSingleItem();
        series.Requests.ShouldBe(0);
        series.Queries.ShouldBe(0);
        series.Allow.ShouldBe(0);
        series.SourceComputeMs.ShouldBe(420);
        series.SourceComputeMeasured.ShouldBe(1);
        series.SourceCostMicros.ShouldBe(2_500_000);
        series.SourceCostMeasured.ShouldBe(1);

        var grant = late.GrantSeries.Values.ShouldHaveSingleItem();
        grant.Accesses.ShouldBe(0);
        grant.Queries.ShouldBe(0);
        grant.SourceComputeMs.ShouldBe(420);
        grant.SourceCostMicros.ShouldBe(2_500_000);
    }

    [Fact]
    public async Task Missing_sample_retries_with_exponential_backoff_and_succeeds_later()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        var aggregator = new UsageAggregator(timeProvider: time);
        int callCount = 0;
        var collector = new FakeCollector("Databricks", "DBU", (_, _) =>
        {
            callCount++;
            if (callCount == 1)
            {
                return Task.FromResult<IReadOnlyList<SourceCostSample>>(Array.Empty<SourceCostSample>());
            }

            return Task.FromResult<IReadOnlyList<SourceCostSample>>(
                [new SourceCostSample("stmt-retry", 150, null, null)]);
        });
        using var worker = CreateWorker(aggregator, [collector], time);

        RecordDatabricksFact(aggregator, "stmt-retry");

        await worker.CollectOnceAsync(CancellationToken.None);
        callCount.ShouldBe(1);
        aggregator.PendingSourceQueryCount.ShouldBe(1);

        // Before 5-minute backoff expires, collector must not be called again.
        time.Advance(TimeSpan.FromMinutes(4));
        await worker.CollectOnceAsync(CancellationToken.None);
        callCount.ShouldBe(1);

        time.Advance(TimeSpan.FromMinutes(2));
        await worker.CollectOnceAsync(CancellationToken.None);
        callCount.ShouldBe(2);
        aggregator.PendingSourceQueryCount.ShouldBe(0);

        var snap = aggregator.Swap();
        snap.Series.Values.ShouldHaveSingleItem().SourceComputeMs.ShouldBe(150);
    }

    [Fact]
    public async Task Expired_queries_older_than_7_days_are_dropped_and_counted()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        var aggregator = new UsageAggregator(timeProvider: time);
        int calls = 0;
        var collector = new FakeCollector("Databricks", "DBU", (_, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<SourceCostSample>>(Array.Empty<SourceCostSample>());
        });
        using var worker = CreateWorker(aggregator, [collector], time);

        long expiredDelta = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, ml) =>
        {
            if (inst.Name == "autheris_usage_source_cost_expired_total") ml.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((_, val, _, _) => Interlocked.Add(ref expiredDelta, val));
        listener.Start();

        RecordDatabricksFact(aggregator, "stmt-expired");
        time.Advance(TimeSpan.FromDays(7).Add(TimeSpan.FromMinutes(1)));

        await worker.CollectOnceAsync(CancellationToken.None);

        calls.ShouldBe(0);
        aggregator.PendingSourceQueryCount.ShouldBe(0);
        expiredDelta.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Pending_queue_bounded_at_max_capacity_drops_overflow()
    {
        UsageAggregator.MaxPendingSourceQueries.ShouldBe(50_000);

        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        var aggregator = new UsageAggregator(timeProvider: time, maxPendingQueries: 3);

        long overflowDelta = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, ml) =>
        {
            if (inst.Name == "autheris_usage_source_cost_queue_overflow_total") ml.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((_, val, _, _) => Interlocked.Add(ref overflowDelta, val));
        listener.Start();

        for (int i = 0; i < 5; i++)
        {
            RecordDatabricksFact(aggregator, $"stmt-{i}");
        }

        aggregator.PendingSourceQueryCount.ShouldBe(3);
        overflowDelta.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Batches_pending_queries_per_tenant_and_data_source_up_to_100()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        var aggregator = new UsageAggregator(timeProvider: time);
        var batchSizes = new List<int>();
        var collector = new FakeCollector("Databricks", "DBU", (batch, _) =>
        {
            batchSizes.Add(batch.Count);
            var samples = batch.Select(q => new SourceCostSample(q.SourceQueryId, 10, null, null)).ToList();
            return Task.FromResult<IReadOnlyList<SourceCostSample>>(samples);
        });
        using var worker = CreateWorker(aggregator, [collector], time);

        for (int i = 0; i < 105; i++)
        {
            RecordDatabricksFact(aggregator, $"stmt-batch-{i}");
        }

        await worker.CollectOnceAsync(CancellationToken.None);

        batchSizes.ShouldBe([100, 5]);
        aggregator.PendingSourceQueryCount.ShouldBe(0);
    }

    private static void RecordDatabricksFact(UsageAggregator aggregator, string statementId)
    {
        aggregator.Record(
            new UsageIdentity(Tenant, UserSid, new HashSet<Sid>()),
            DataSourceTransportFlags.Rest,
            null,
            10,
            64,
            200,
            [new UsageFact(
                TableId,
                "dbx-main",
                UsageOutcome.Allow,
                1,
                true,
                0,
                Array.Empty<GrantRef>(),
                new SourceExecutionStats(null, null, null, statementId))]);
    }

    private static SourceCostCollectorWorker CreateWorker(
        IUsageAggregator aggregator,
        IEnumerable<ISourceCostCollector> collectors,
        TimeProvider timeProvider)
    {
        var gateway = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["dbx-main"] = new() { Provider = "Databricks" }
                }
            }
        };

        return new SourceCostCollectorWorker(
            aggregator,
            collectors,
            Options.Create(gateway),
            new NullLogger<SourceCostCollectorWorker>(),
            timeProvider);
    }

    private sealed class FakeCollector(
        string sourceType,
        string costUnit,
        Func<IReadOnlyList<PendingSourceQuery>, CancellationToken, Task<IReadOnlyList<SourceCostSample>>> handler) : ISourceCostCollector
    {
        public string SourceType => sourceType;
        public string CostUnit => costUnit;
        public Task<IReadOnlyList<SourceCostSample>> CollectAsync(IReadOnlyList<PendingSourceQuery> pending, CancellationToken ct) =>
            handler(pending, ct);
    }

    private sealed class NullLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
