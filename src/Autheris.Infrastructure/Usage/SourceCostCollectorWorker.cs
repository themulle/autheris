namespace Autheris.Infrastructure.Usage;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>Periodically collects delayed source-side compute and cost telemetry for pending queries.</summary>
public sealed class SourceCostCollectorWorker : BackgroundService
{
    public static readonly TimeSpan DefaultCollectionInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxSampleAge = TimeSpan.FromDays(7);
    public const int MaxBatchSize = 100;
    public const long MaxComputeMs = 1_000_000_000_000L;
    public const decimal MaxSourceCost = 1_000_000_000_000m;

    private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(6);

    private readonly IUsageAggregator _aggregator;
    private readonly IReadOnlyList<ISourceCostCollector> _collectors;
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly ILogger<SourceCostCollectorWorker> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _collectionInterval;

    public SourceCostCollectorWorker(
        IUsageAggregator aggregator,
        IEnumerable<ISourceCostCollector> collectors,
        IOptions<GatewayOptions> gatewayOptions,
        ILogger<SourceCostCollectorWorker> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? collectionInterval = null)
    {
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        ArgumentNullException.ThrowIfNull(collectors);
        _collectors = collectors as IReadOnlyList<ISourceCostCollector> ?? [.. collectors];
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _collectionInterval = collectionInterval is { } interval && interval > TimeSpan.Zero
            ? interval
            : DefaultCollectionInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_collectionInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await CollectOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task CollectOnceAsync(CancellationToken ct)
    {
        var drained = _aggregator.DrainPendingSourceQueries(UsageAggregator.MaxPendingSourceQueries);
        if (drained.Count == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var requeue = new List<PendingSourceQuery>();
        var grouped = new Dictionary<(ISourceCostCollector Collector, string DataSource, TenantId Tenant), List<PendingSourceQuery>>();

        for (int i = 0; i < drained.Count; i++)
        {
            var item = drained[i];
            if (now - item.EnqueuedAt > MaxSampleAge)
            {
                UsageDiagnostics.SourceCostExpiredCounter.Add(1);
                continue;
            }

            if (item.NextAttemptAt.HasValue && item.NextAttemptAt.Value > now)
            {
                requeue.Add(item);
                continue;
            }

            var collector = ResolveCollector(item.DataSource);
            if (collector is null)
            {
                continue;
            }

            var groupKey = (collector, item.DataSource, item.Tenant);
            if (!grouped.TryGetValue(groupKey, out var list))
            {
                list = new List<PendingSourceQuery>();
                grouped[groupKey] = list;
            }

            list.Add(item);
        }

        var groups = new List<((ISourceCostCollector Collector, string DataSource, TenantId Tenant) Key, List<PendingSourceQuery> Items)>(grouped.Count);
        foreach (var kv in grouped)
        {
            groups.Add((kv.Key, kv.Value));
        }

        for (int g = 0; g < groups.Count; g++)
        {
            var (groupKey, items) = groups[g];
            for (int offset = 0; offset < items.Count; offset += MaxBatchSize)
            {
                int count = Math.Min(MaxBatchSize, items.Count - offset);
                var batch = items.GetRange(offset, count);

                if (ct.IsCancellationRequested)
                {
                    RequeueRemaining(groups, g, offset, requeue);
                    return;
                }

                try
                {
                    var samples = await groupKey.Collector.CollectAsync(batch, ct).ConfigureAwait(false);
                    ApplySamples(batch, samples, groupKey.Collector, now, requeue);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    RequeueRemaining(groups, g, offset, requeue);
                    return;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    UsageDiagnostics.SourceCostErrorsCounter.Add(1);
                    _logger.LogWarning(
                        "Source cost collection batch failed (batchSize={BatchSize}, exception={ExceptionType}).",
                        batch.Count,
                        ex.GetType().Name);

                    for (int i = 0; i < batch.Count; i++)
                    {
                        ScheduleRetry(batch[i], now, requeue);
                    }
                }
            }
        }

        for (int i = 0; i < requeue.Count; i++)
        {
            _aggregator.TryEnqueuePendingSourceQuery(requeue[i]);
        }
    }

    private void ApplySamples(
        List<PendingSourceQuery> batch,
        IReadOnlyList<SourceCostSample> samples,
        ISourceCostCollector collector,
        DateTimeOffset now,
        List<PendingSourceQuery> requeue)
    {
        var byQueryId = new Dictionary<string, SourceCostSample>(samples.Count, StringComparer.Ordinal);
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (!string.IsNullOrWhiteSpace(s.SourceQueryId))
            {
                byQueryId[s.SourceQueryId] = s;
            }
        }

        for (int i = 0; i < batch.Count; i++)
        {
            var item = batch[i];
            if (!byQueryId.TryGetValue(item.SourceQueryId, out var sample))
            {
                ScheduleRetry(item, now, requeue);
                continue;
            }

            if (!IsSampleWithinBounds(sample))
            {
                UsageDiagnostics.SourceCostMalformedCounter.Add(1);
                continue;
            }

            long? costMicros = null;
            if (sample.SourceCost.HasValue)
            {
                string effectiveUnit = string.IsNullOrWhiteSpace(sample.CostUnit) ? collector.CostUnit : sample.CostUnit.Trim();
                bool matchesCollector = string.Equals(effectiveUnit, collector.CostUnit, StringComparison.OrdinalIgnoreCase);
                bool matchesTariff = _aggregator.IsSourceCostUnitValid(item.DataSource, effectiveUnit);
                if (!matchesCollector || !matchesTariff)
                {
                    UsageDiagnostics.SourceUnitMismatchCounter.Add(1);
                }
                else
                {
                    costMicros = (long)Math.Round(sample.SourceCost.Value * 1_000_000m, MidpointRounding.AwayFromZero);
                }
            }

            if (sample.SourceComputeMs.HasValue || costMicros.HasValue)
            {
                _aggregator.AggregateSourceStats(item.SeriesKey, item.GrantIds, sample.SourceComputeMs, costMicros);
                UsageDiagnostics.SourceCostCollectedCounter.Add(1);
            }
        }
    }

    private static bool IsSampleWithinBounds(SourceCostSample sample)
    {
        if (sample.SourceComputeMs is { } computeMs && (computeMs < 0 || computeMs > MaxComputeMs))
        {
            return false;
        }

        if (sample.SourceCost is { } cost && (cost < 0m || cost > MaxSourceCost))
        {
            return false;
        }

        return true;
    }

    private static void ScheduleRetry(PendingSourceQuery item, DateTimeOffset now, List<PendingSourceQuery> requeue)
    {
        int nextAttempt = item.AttemptCount + 1;
        int shift = Math.Clamp(nextAttempt - 1, 0, 10);
        long delayTicks = BaseRetryDelay.Ticks << shift;
        var delay = delayTicks > MaxRetryDelay.Ticks ? MaxRetryDelay : TimeSpan.FromTicks(delayTicks);
        requeue.Add(item with { AttemptCount = nextAttempt, NextAttemptAt = now + delay });
    }

    private void RequeueRemaining(
        List<((ISourceCostCollector Collector, string DataSource, TenantId Tenant) Key, List<PendingSourceQuery> Items)> groups,
        int currentGroupIndex,
        int currentOffset,
        List<PendingSourceQuery> requeue)
    {
        for (int g = currentGroupIndex; g < groups.Count; g++)
        {
            var items = groups[g].Items;
            int start = g == currentGroupIndex ? currentOffset : 0;
            for (int i = start; i < items.Count; i++)
            {
                requeue.Add(items[i]);
            }
        }

        for (int i = 0; i < requeue.Count; i++)
        {
            _aggregator.TryEnqueuePendingSourceQuery(requeue[i]);
        }
    }

    private ISourceCostCollector? ResolveCollector(string dataSource) =>
        SourceCostCollectorResolver.FindForDataSource(dataSource, _gatewayOptions.Value, _collectors);
}
