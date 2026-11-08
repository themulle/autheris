namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

public sealed class UsageRequestScopeTests
{
    [Fact]
    public void Fact_added_to_active_scope()
    {
        var identity = new UsageIdentity(new TenantId("t1"), new Sid("S-1-5-21-user1"), new HashSet<Sid>());
        var aggregator = new UsageAggregator();
        var recorder = new UsageRecorder(aggregator);

        using (var scope = UsageRequestScope.Begin(identity, DataSourceTransportFlags.Rest, null, aggregator))
        {
            UsageRequestScope.Current.ShouldBe(scope);

            var fact = new UsageFact(
                new TableIdentifier("db", "sch", "tbl"),
                "ds1",
                UsageOutcome.Allow,
                Rows: 100,
                RowsMeasured: true,
                AnalysisCost: 5,
                Grants: [],
                Source: default);

            recorder.Record(fact, identity.Tenant, identity.UserSid);

            scope.FactCount.ShouldBe(1);
            scope.Facts[0].Table.TableName.ShouldBe("tbl");
        }

        UsageRequestScope.Current.ShouldBeNull();
    }

    [Fact]
    public void Fact_without_scope_recorded_unscoped()
    {
        var aggregator = new UsageAggregator();
        var recorder = new UsageRecorder(aggregator);

        UsageRequestScope.Current.ShouldBeNull();

        var tenant = new TenantId("t-unscoped");
        var actor = new Sid("S-1-5-21-unscoped");
        var table = new TableIdentifier("db", "sch", "tbl_unscoped");

        var fact = new UsageFact(
            table,
            "ds_unscoped",
            UsageOutcome.Allow,
            Rows: 42,
            RowsMeasured: true,
            AnalysisCost: 2,
            Grants: [],
            Source: default);

        recorder.Record(fact, tenant, actor);

        var snap = aggregator.Swap();
        snap.Series.Count.ShouldBe(1);

        var row = snap.Series.Values.GetEnumerator();
        row.MoveNext().ShouldBeTrue();
        row.Current.Requests.ShouldBe(1);
        row.Current.Queries.ShouldBe(1);
        row.Current.Rows.ShouldBe(42);
    }

    [Fact]
    public void Fact_with_mismatching_actor_not_added_to_scope()
    {
        var identity = new UsageIdentity(new TenantId("t1"), new Sid("S-1-5-21-user1"), new HashSet<Sid>());
        var aggregator = new UsageAggregator();
        var recorder = new UsageRecorder(aggregator);

        using (var scope = UsageRequestScope.Begin(identity, DataSourceTransportFlags.Rest, null, aggregator))
        {
            var fact = new UsageFact(
                new TableIdentifier("db", "sch", "tbl"),
                "ds1",
                UsageOutcome.Allow,
                Rows: 50,
                RowsMeasured: true,
                AnalysisCost: 1,
                Grants: [],
                Source: default);

            // Record with different actor -> must NOT be added to active scope, goes to unscoped
            recorder.Record(fact, identity.Tenant, new Sid("S-1-5-21-other-user"));

            scope.FactCount.ShouldBe(0);
        }

        var snap = aggregator.Swap();
        snap.Series.Count.ShouldBe(1);
    }

    [Fact]
    public void Complete_is_idempotent()
    {
        var identity = new UsageIdentity(new TenantId("t1"), new Sid("S-1-5-21-user1"), new HashSet<Sid>());
        var aggregator = new UsageAggregator();

        using (var scope = UsageRequestScope.Begin(identity, DataSourceTransportFlags.Rest, null, aggregator))
        {
            var fact = new UsageFact(
                new TableIdentifier("db", "sch", "tbl"),
                "ds1",
                UsageOutcome.Allow,
                Rows: 10,
                RowsMeasured: true,
                AnalysisCost: 0,
                Grants: [],
                Source: default);

            scope.AddFact(fact);

            scope.Complete(durationMs: 50, bytes: 1024, statusCode: 200);
            scope.Complete(durationMs: 999, bytes: 9999, statusCode: 500); // 2nd call should be no-op

            scope.IsCompleted.ShouldBeTrue();
        }

        var snap = aggregator.Swap();
        snap.Series.Count.ShouldBe(1);

        var counters = snap.Series.Values.GetEnumerator();
        counters.MoveNext().ShouldBeTrue();
        counters.Current.Requests.ShouldBe(1);
        counters.Current.DurationMs.ShouldBe(50);
        counters.Current.Bytes.ShouldBe(1024);
        counters.Current.Allow.ShouldBe(1);
        counters.Current.Errors.ShouldBe(0);
    }

    [Fact]
    public void Fact_buffer_overflow_merges_into_same_table_or_last_fact()
    {
        var identity = new UsageIdentity(new TenantId("t1"), new Sid("S-1-5-21-user1"), new HashSet<Sid>());
        var aggregator = new UsageAggregator();

        using (var scope = UsageRequestScope.Begin(identity, DataSourceTransportFlags.Rest, null, aggregator))
        {
            var targetTable = new TableIdentifier("sales", "dbo", "orders");

            // Fill 64 facts: first 63 are other tables, fact 63 (index 63) is targetTable
            for (int i = 0; i < 63; i++)
            {
                scope.AddFact(new UsageFact(
                    new TableIdentifier("sales", "dbo", $"table_{i}"),
                    "ds",
                    UsageOutcome.Allow,
                    Rows: 1,
                    RowsMeasured: true,
                    AnalysisCost: 1,
                    Grants: [],
                    Source: default));
            }

            scope.AddFact(new UsageFact(
                targetTable,
                "ds",
                UsageOutcome.Allow,
                Rows: 10,
                RowsMeasured: true,
                AnalysisCost: 5,
                Grants: [new GrantRef("g1", null, null)],
                Source: default));

            scope.FactCount.ShouldBe(64);

            // Add 65th fact for targetTable -> should be merged into the existing targetTable fact
            scope.AddFact(new UsageFact(
                targetTable,
                "ds",
                UsageOutcome.Allow,
                Rows: 25,
                RowsMeasured: true,
                AnalysisCost: 15,
                Grants: [new GrantRef("g2", null, null)],
                Source: default));

            scope.FactCount.ShouldBe(64);

            var lastFact = scope.Facts[63];
            lastFact.Table.ShouldBe(targetTable);
            lastFact.Rows.ShouldBe(35);
            lastFact.AnalysisCost.ShouldBe(20);
            lastFact.Grants.Count.ShouldBe(2);
        }
    }
}
