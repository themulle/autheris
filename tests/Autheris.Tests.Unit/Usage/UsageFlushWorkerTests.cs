namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Usage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class UsageFlushWorkerTests
{
    private static readonly TenantId Tenant = new("t1");
    private static readonly Sid UserSid = new("S-1-5-21-100-200-300");
    private static readonly TableIdentifier TableId = new("sales", "dbo", "orders");

    [Fact]
    public async Task Failed_flush_requeues_generation()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        var opts = Options.Create(new UsageAccountingOptions { MaxFlushRetries = 3 });
        var agg = new UsageAggregator(opts, timeProvider: time);
        var repo = new FakeUsageRepository { ThrowOnAccumulate = new InvalidOperationException("transient") };
        using var worker = new UsageFlushWorker(agg, repo, opts, new CapturingLogger<UsageFlushWorker>(), time);

        agg.Record(
            new UsageIdentity(Tenant, UserSid, new HashSet<Sid>()),
            DataSourceTransportFlags.Rest,
            null,
            15,
            256,
            200,
            [new UsageFact(TableId, "pg-main", UsageOutcome.Allow, 5, true, 0, Array.Empty<GrantRef>(), default)]);

        await worker.FlushOnceAsync(CancellationToken.None);
        repo.Accumulated.Count.ShouldBe(0);

        repo.ThrowOnAccumulate = null;
        await worker.FlushOnceAsync(CancellationToken.None);

        repo.Accumulated.Count.ShouldBe(1);
        var series = repo.Accumulated[0].Series.Values;
        series.ShouldHaveSingleItem().Requests.ShouldBe(1);
        series.ShouldHaveSingleItem().Rows.ShouldBe(5);
    }

    [Fact]
    public async Task Drops_after_max_retries_and_counts()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        var opts = Options.Create(new UsageAccountingOptions { MaxFlushRetries = 2 });
        var agg = new UsageAggregator(opts, timeProvider: time);
        var repo = new FakeUsageRepository { ThrowOnAccumulate = new InvalidOperationException("down") };
        using var worker = new UsageFlushWorker(agg, repo, opts, new CapturingLogger<UsageFlushWorker>(), time);

        long dropped = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (inst, ml) =>
        {
            if (inst.Name == "autheris_usage_dropped_total") ml.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((_, val, _, _) => Interlocked.Add(ref dropped, val));
        listener.Start();

        agg.Record(
            new UsageIdentity(Tenant, UserSid, new HashSet<Sid>()),
            DataSourceTransportFlags.Rest,
            null,
            10,
            64,
            200,
            [new UsageFact(TableId, "pg-main", UsageOutcome.Allow, 2, true, 0, Array.Empty<GrantRef>(), default)]);

        await worker.FlushOnceAsync(CancellationToken.None);
        dropped.ShouldBe(0);

        await worker.FlushOnceAsync(CancellationToken.None);
        dropped.ShouldBeGreaterThanOrEqualTo(1);

        repo.ThrowOnAccumulate = null;
        await worker.FlushOnceAsync(CancellationToken.None);
        repo.Accumulated.ShouldBeEmpty();
    }

    [Fact]
    public async Task Final_flush_on_stop()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        var opts = Options.Create(new UsageAccountingOptions { FlushIntervalSeconds = 60 });
        var agg = new UsageAggregator(opts, timeProvider: time);
        var repo = new FakeUsageRepository();
        using var worker = new UsageFlushWorker(agg, repo, opts, new CapturingLogger<UsageFlushWorker>(), time);

        await worker.StartAsync(CancellationToken.None);
        agg.Record(
            new UsageIdentity(Tenant, UserSid, new HashSet<Sid>()),
            DataSourceTransportFlags.GraphQL,
            null,
            5,
            32,
            200,
            [new UsageFact(TableId, "pg-main", UsageOutcome.Allow, 1, true, 0, Array.Empty<GrantRef>(), default)]);

        await worker.StopAsync(CancellationToken.None);
        repo.Accumulated.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Purges_daily_with_retention()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var opts = Options.Create(new UsageAccountingOptions { RetentionDays = 90 });
        var agg = new UsageAggregator(opts, timeProvider: time);
        var repo = new FakeUsageRepository();
        using var worker = new UsageFlushWorker(agg, repo, opts, new CapturingLogger<UsageFlushWorker>(), time);

        await worker.PurgeIfDueAsync(CancellationToken.None);
        await worker.PurgeIfDueAsync(CancellationToken.None);
        repo.PurgeCutoffs.Count.ShouldBe(1);
        repo.PurgeCutoffs[0].ShouldBe(new DateOnly(2026, 11, 15).AddDays(-90));

        time.Advance(TimeSpan.FromDays(1));
        await worker.PurgeIfDueAsync(CancellationToken.None);
        repo.PurgeCutoffs.Count.ShouldBe(2);
        repo.PurgeCutoffs[1].ShouldBe(new DateOnly(2026, 11, 16).AddDays(-90));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Logs_never_contain_user_sids()
    {
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero));
        var opts = Options.Create(new UsageAccountingOptions { MaxFlushRetries = 1 });
        var agg = new UsageAggregator(opts, timeProvider: time);
        var logger = new CapturingLogger<UsageFlushWorker>();
        var repo = new FakeUsageRepository { ThrowOnAccumulate = new InvalidOperationException("bad actor " + UserSid.Value) };
        using var worker = new UsageFlushWorker(agg, repo, opts, logger, time);

        agg.Record(
            new UsageIdentity(Tenant, UserSid, new HashSet<Sid>()),
            DataSourceTransportFlags.Rest,
            null,
            10,
            64,
            200,
            [new UsageFact(TableId, "pg-main", UsageOutcome.Allow, 1, true, 0, Array.Empty<GrantRef>(), default)]);

        await worker.FlushOnceAsync(CancellationToken.None);

        logger.Messages.ShouldNotBeEmpty();
        foreach (string msg in logger.Messages)
        {
            msg.ShouldNotContain("S-1-5-21", Case.Insensitive);
        }
    }

    private sealed class FakeUsageRepository : IUsageRepository
    {
        public List<UsageGenerationSnapshot> Accumulated { get; } = new();
        public List<DateOnly> PurgeCutoffs { get; } = new();
        public Exception? ThrowOnAccumulate { get; set; }

        public Task AccumulateAsync(UsageGenerationSnapshot snapshot, CancellationToken ct = default)
        {
            if (ThrowOnAccumulate != null) throw ThrowOnAccumulate;
            Accumulated.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task<UsageReportPage> QueryAsync(UsageReportQuery query, CancellationToken ct = default) =>
            Task.FromResult(new UsageReportPage(Array.Empty<UsageReportRow>(), null));

        public Task<int> PurgeAsync(DateOnly cutoffExclusive, int batchSize = 10_000, CancellationToken ct = default)
        {
            PurgeCutoffs.Add(cutoffExclusive);
            return Task.FromResult(0);
        }

        public Task<int> EraseUserAsync(TenantId tenant, string userKey, CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<UsageTariffVersion>> GetTariffsAsync(TenantId tenant, DateOnly fromUtc, DateOnly toUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UsageTariffVersion>>(Array.Empty<UsageTariffVersion>());
        public Task<UsageTariffVersion> AddTariffVersionAsync(UsageTariffVersion tariff, CancellationToken ct = default) => Task.FromResult(tariff);
        public Task<UsageGrantRefRecord?> GetGrantRefAsync(TenantId tenant, string grantId, CancellationToken ct = default) =>
            Task.FromResult<UsageGrantRefRecord?>(null);
        public Task<IReadOnlyList<ConsumerAppDailySummary>> QueryConsumerAppDailyAsync(TableIdentifier tableId, TenantId tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConsumerAppDailySummary>>(Array.Empty<ConsumerAppDailySummary>());
        public Task<IReadOnlyDictionary<string, int>> QueryDistinctActorCountsAsync(TableIdentifier tableId, TenantId tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
