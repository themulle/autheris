namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class UsageAggregatorTests
{
    [Fact]
    public void Same_key_accumulates_all_counters()
    {
        var aggregator = new UsageAggregator();
        var key = new UsageSeriesKey(
            new TenantId("t1"),
            new DateOnly(2026, 11, 8),
            "user1",
            "groupSet1",
            "bg1",
            DataSourceTransportFlags.Rest,
            "app1",
            "client1",
            "source1",
            "default.public.tbl");

        aggregator.Aggregate(
            key,
            requests: 1,
            queries: 2,
            rows: 100,
            rowsMeasured: true,
            bytes: 2048,
            durationMs: 50,
            analysisCost: 10,
            sourceComputeMs: 30,
            sourceCostMicros: 500_000,
            outcome: UsageOutcome.Allow);

        aggregator.Aggregate(
            key,
            requests: 2,
            queries: 3,
            rows: 150,
            rowsMeasured: true,
            bytes: 4096,
            durationMs: 75,
            analysisCost: 20,
            sourceComputeMs: 40,
            sourceCostMicros: 250_000,
            outcome: UsageOutcome.Allow);

        var snapshot = aggregator.Swap();

        snapshot.Series.Count.ShouldBe(1);
        snapshot.Series.TryGetValue(key, out var counters).ShouldBeTrue();
        counters.ShouldNotBeNull();
        counters.Requests.ShouldBe(3);
        counters.Queries.ShouldBe(5);
        counters.Rows.ShouldBe(250);
        counters.RowsMeasured.ShouldBe(2);
        counters.Bytes.ShouldBe(6144);
        counters.DurationMs.ShouldBe(125);
        counters.AnalysisCost.ShouldBe(30);
        counters.SourceComputeMs.ShouldBe(70);
        counters.SourceComputeMeasured.ShouldBe(2);
        counters.SourceCostMicros.ShouldBe(750_000);
        counters.SourceCostMeasured.ShouldBe(2);
        counters.Allow.ShouldBe(2);
        counters.DenyAccess.ShouldBe(0);
    }

    [Fact]
    public async Task Swap_waits_for_active_writers_and_loses_nothing()
    {
        var aggregator = new UsageAggregator();
        var key = new UsageSeriesKey(
            new TenantId("t1"),
            new DateOnly(2026, 11, 8),
            "u1",
            "none",
            "unassigned",
            DataSourceTransportFlags.Rest,
            "app",
            "client",
            "ds",
            "table");

        const int threadCount = 8;
        const int addsPerThread = 100_000;
        long totalExpectedAdds = (long)threadCount * addsPerThread;

        var snapshots = new List<UsageGenerationSnapshot>();
        var snapshotLock = new object();

        var swapTask = Task.Run(async () =>
        {
            for (int i = 0; i < 5; i++)
            {
                await Task.Yield();
                var snap = aggregator.Swap();
                lock (snapshotLock)
                {
                    snapshots.Add(snap);
                }
            }
        });

        Parallel.For(0, threadCount, _ =>
        {
            for (int i = 0; i < addsPerThread; i++)
            {
                aggregator.Aggregate(
                    key,
                    requests: 1,
                    queries: 1,
                    rows: 1,
                    rowsMeasured: false,
                    bytes: 1,
                    durationMs: 1,
                    analysisCost: 0,
                    sourceComputeMs: null,
                    sourceCostMicros: null,
                    outcome: UsageOutcome.Allow);
            }
        });

        await swapTask;

        var finalSnap = aggregator.Swap();
        lock (snapshotLock)
        {
            snapshots.Add(finalSnap);
        }

        long actualRequests = 0;
        foreach (var snap in snapshots)
        {
            if (snap.Series.TryGetValue(key, out var counters))
            {
                actualRequests += counters.Requests;
            }
        }

        actualRequests.ShouldBe(totalExpectedAdds);
    }

    [Fact]
    public void Overflow_folds_into_other_bucket()
    {
        var options = new UsageAccountingOptions
        {
            MaxSeries = 10
        };
        var aggregator = new UsageAggregator(Options.Create(options));
        var tenant = new TenantId("t1");
        var day = new DateOnly(2026, 11, 8);

        // Fill up to MaxSeries (10)
        for (int i = 0; i < 10; i++)
        {
            var k = new UsageSeriesKey(
                tenant,
                day,
                "u1",
                "none",
                "unassigned",
                DataSourceTransportFlags.Rest,
                "app1",
                "client1",
                "ds1",
                $"table-{i}");

            aggregator.Aggregate(
                k,
                requests: 1,
                queries: 1,
                rows: 10,
                rowsMeasured: false,
                bytes: 100,
                durationMs: 10,
                analysisCost: 0,
                sourceComputeMs: null,
                sourceCostMicros: null,
                outcome: UsageOutcome.Allow);
        }

        // Add 11th key for same user/datasource - beyond capacity -> should fold into *other*
        var overflowKey = new UsageSeriesKey(
            tenant,
            day,
            "u1",
            "none",
            "unassigned",
            DataSourceTransportFlags.Rest,
            "app1",
            "client1",
            "ds1",
            "table-overflow-1");

        aggregator.Aggregate(
            overflowKey,
            requests: 1,
            queries: 1,
            rows: 50,
            rowsMeasured: false,
            bytes: 500,
            durationMs: 25,
            analysisCost: 0,
            sourceComputeMs: null,
            sourceCostMicros: null,
            outcome: UsageOutcome.Allow);

        var snap = aggregator.Swap();

        var expectedFoldedKey = overflowKey with
        {
            TableId = "*other*",
            Application = "*other*",
            ClientId = string.Empty
        };

        snap.Series.ContainsKey(expectedFoldedKey).ShouldBeTrue();
        snap.Series[expectedFoldedKey].Rows.ShouldBe(50);
        snap.Series.ContainsKey(overflowKey).ShouldBeFalse();
    }

    [Fact]
    public void Drop_only_beyond_overflow_headroom_and_counted()
    {
        var options = new UsageAccountingOptions
        {
            MaxSeries = 10 // headroom is max(1, 10/100) = 1, so max 11 entries
        };
        var aggregator = new UsageAggregator(Options.Create(options));
        var tenant = new TenantId("t1");
        var day = new DateOnly(2026, 11, 8);

        // Fill up to MaxSeries (10)
        for (int i = 0; i < 10; i++)
        {
            var k = new UsageSeriesKey(
                tenant,
                day,
                $"u-{i}",
                "none",
                "unassigned",
                DataSourceTransportFlags.Rest,
                "app",
                "client",
                "ds",
                $"table-{i}");

            aggregator.Aggregate(
                k,
                requests: 1,
                queries: 1,
                rows: 1,
                rowsMeasured: false,
                bytes: 1,
                durationMs: 1,
                analysisCost: 0,
                sourceComputeMs: null,
                sourceCostMicros: null,
                outcome: UsageOutcome.Allow);
        }

        // Add 11th distinct key (user u-11) -> creates folded key in 1% headroom (entry 11)
        var k11 = new UsageSeriesKey(
            tenant,
            day,
            "u-11",
            "none",
            "unassigned",
            DataSourceTransportFlags.Rest,
            "app",
            "client",
            "ds",
            "table-x");

        aggregator.Aggregate(
            k11,
            requests: 1,
            queries: 1,
            rows: 1,
            rowsMeasured: false,
            bytes: 1,
            durationMs: 1,
            analysisCost: 0,
            sourceComputeMs: null,
            sourceCostMicros: null,
            outcome: UsageOutcome.Allow);

        // Now count is 11 (MaxSeries + headroom).
        // Adding another distinct user (u-12) will exceed headroom and be dropped!
        var k12 = new UsageSeriesKey(
            tenant,
            day,
            "u-12",
            "none",
            "unassigned",
            DataSourceTransportFlags.Rest,
            "app",
            "client",
            "ds",
            "table-y");

        aggregator.Aggregate(
            k12,
            requests: 1,
            queries: 1,
            rows: 1,
            rowsMeasured: false,
            bytes: 1,
            durationMs: 1,
            analysisCost: 0,
            sourceComputeMs: null,
            sourceCostMicros: null,
            outcome: UsageOutcome.Allow);

        var snap = aggregator.Swap();
        snap.Series.Count.ShouldBe(11);

        var droppedFoldedKey = k12 with { TableId = "*other*", Application = "*other*", ClientId = string.Empty };
        snap.Series.ContainsKey(droppedFoldedKey).ShouldBeFalse();
    }

    [Fact]
    public void Request_metrics_attributed_to_first_fact_table()
    {
        var aggregator = new UsageAggregator();
        var identity = new UsageIdentity(new TenantId("t1"), new Sid("S-1-5-21-user"), new HashSet<Sid>());
        var table1 = new TableIdentifier("sales", "dbo", "orders");
        var table2 = new TableIdentifier("sales", "dbo", "order_items");

        var fact1 = new UsageFact(
            table1,
            "ds_orders",
            UsageOutcome.Allow,
            Rows: 10,
            RowsMeasured: true,
            AnalysisCost: 5,
            Grants: [new GrantRef("grant-1", null, null)],
            Source: default);

        var fact2 = new UsageFact(
            table2,
            "ds_orders",
            UsageOutcome.Allow,
            Rows: 50,
            RowsMeasured: true,
            AnalysisCost: 3,
            Grants: [new GrantRef("grant-2", null, null)],
            Source: default);

        aggregator.Record(
            identity,
            DataSourceTransportFlags.GraphQL,
            caller: new CallerContext("testApp", "v1", "query1", "clientA"),
            durationMs: 120,
            bytes: 8192,
            statusCode: 200,
            facts: [fact1, fact2]);

        var snap = aggregator.Swap();

        var day = DateOnly.FromDateTime(DateTime.UtcNow);
        var key1 = new UsageSeriesKey(
            new TenantId("t1"),
            day,
            "S-1-5-21-user",
            UsageAttribution.NoneGroupSetId,
            UsageAttribution.UnassignedBillingGroup,
            DataSourceTransportFlags.GraphQL,
            "testapp",
            "clienta",
            "ds_orders",
            table1.ToString());

        var key2 = new UsageSeriesKey(
            new TenantId("t1"),
            day,
            "S-1-5-21-user",
            UsageAttribution.NoneGroupSetId,
            UsageAttribution.UnassignedBillingGroup,
            DataSourceTransportFlags.GraphQL,
            "testapp",
            "clienta",
            "ds_orders",
            table2.ToString());

        snap.Series.TryGetValue(key1, out var c1).ShouldBeTrue();
        c1.ShouldNotBeNull();
        c1.Requests.ShouldBe(1);
        c1.DurationMs.ShouldBe(120);
        c1.Bytes.ShouldBe(8192);
        c1.Queries.ShouldBe(1);
        c1.Rows.ShouldBe(10);
        c1.AnalysisCost.ShouldBe(5);

        snap.Series.TryGetValue(key2, out var c2).ShouldBeTrue();
        c2.ShouldNotBeNull();
        c2.Requests.ShouldBe(0);
        c2.DurationMs.ShouldBe(0);
        c2.Bytes.ShouldBe(0);
        c2.Queries.ShouldBe(1);
        c2.Rows.ShouldBe(50);
        c2.AnalysisCost.ShouldBe(3);
    }

    [Fact]
    public void Request_without_fact_uses_empty_table()
    {
        var aggregator = new UsageAggregator();
        var identity = new UsageIdentity(new TenantId("t1"), new Sid("S-1-5-21-user"), new HashSet<Sid>());

        aggregator.Record(
            identity,
            DataSourceTransportFlags.Rest,
            caller: new CallerContext("app", "v1", "q", "client"),
            durationMs: 45,
            bytes: 512,
            statusCode: 403,
            facts: []);

        var snap = aggregator.Swap();
        var day = DateOnly.FromDateTime(DateTime.UtcNow);

        var key = new UsageSeriesKey(
            new TenantId("t1"),
            day,
            "S-1-5-21-user",
            UsageAttribution.NoneGroupSetId,
            UsageAttribution.UnassignedBillingGroup,
            DataSourceTransportFlags.Rest,
            "app",
            "client",
            string.Empty,
            string.Empty);

        snap.Series.TryGetValue(key, out var counters).ShouldBeTrue();
        counters.ShouldNotBeNull();
        counters.Requests.ShouldBe(1);
        counters.Queries.ShouldBe(0);
        counters.DurationMs.ShouldBe(45);
        counters.Bytes.ShouldBe(512);
        counters.DenyAccess.ShouldBe(1);
        counters.Allow.ShouldBe(0);
    }
}
