namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Policy;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

public sealed class UsageGrantAttributionTests
{
    private sealed class CapturingAuditWriter : IAuditWriter
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    [Fact]
    public async Task Allow_fact_carries_grant_from_decision()
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var validFrom = now.AddDays(-1);
        var validUntil = now.AddDays(30);
        var aggregator = new UsageAggregator(timeProvider: new FixedTimeProvider(now));
        var recorder = new UsageRecorder(aggregator);
        var audit = new CapturingAuditWriter();

        var tenant = new TenantId("tenant-grants");
        var actor = new Sid("S-1-5-21-USER-1");
        var table = new TableIdentifier("sales", "dbo", "customers");
        var grantId = "consent-rowless-col-001";

        // Row-less column/table grant decision without RowPolicy
        var decision = TableAccessDecision.Allowed(
            table,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = ColumnAccessLevel.Clear,
                ["email"] = ColumnAccessLevel.Mask
            },
            rowFilterSql: null,
            hasUnconstrainedColumnAllow: false) with
        {
            Grants = [new DecisionGrantRef(grantId, GrantSourceKind.ConsentRequest, validFrom, validUntil)]
        };

        await GovernedQueryStages.RecordTableReadAsync(
            audit,
            tenant,
            actor,
            table,
            allowed: true,
            GovernedQueryStages.DecisionDetails(decision),
            CancellationToken.None,
            usageRecorder: recorder,
            dataSource: "sales-pg",
            grants: decision.Grants);

        var snap = aggregator.Swap();
        snap.GrantSeries.Count.ShouldBe(1);
        var grantEntry = snap.GrantSeries.Single();
        grantEntry.Key.GrantId.ShouldBe(grantId);
        grantEntry.Key.DataSource.ShouldBe("sales-pg");
        grantEntry.Key.TableId.ShouldBe(table.ToString());
        grantEntry.Value.Accesses.ShouldBe(1);
        grantEntry.Value.Queries.ShouldBe(1);

        snap.GrantRefs.TryGetValue((tenant, grantId), out var storedGrant).ShouldBeTrue();
        storedGrant.GrantId.ShouldBe(grantId);
        storedGrant.ValidFrom.ShouldBe(validFrom);
        storedGrant.ValidUntil.ShouldBe(validUntil);
    }

    [Fact]
    public void Allow_fact_counts_every_contributing_grant_in_full_and_user_total_once()
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var validFrom = now.AddDays(-2);
        var validUntil = now.AddDays(10);
        var aggregator = new UsageAggregator(timeProvider: new FixedTimeProvider(now));
        var recorder = new UsageRecorder(aggregator);

        var tenant = new TenantId("tenant-od42");
        var actor = new Sid("S-1-5-21-USER-OD42");
        var table = new TableIdentifier("sales", "dbo", "orders");
        var identity = new UsageIdentity(tenant, actor, new HashSet<Sid>());

        var decision = TableAccessDecision.Allowed(
            table,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase),
            rowFilterSql: null,
            hasUnconstrainedColumnAllow: true) with
        {
            Grants =
            [
                new DecisionGrantRef("grant-alpha", GrantSourceKind.ConsentRequest, validFrom, validUntil),
                new DecisionGrantRef("grant-beta", GrantSourceKind.ConsentRequest, validFrom, validUntil.AddDays(5))
            ]
        };

        using (var scope = UsageRequestScope.Begin(identity, DataSourceTransportFlags.Rest, null, aggregator))
        {
            var fact = new UsageFact(
                table,
                "sales-pg",
                UsageOutcome.Allow,
                Rows: 25,
                RowsMeasured: true,
                AnalysisCost: 7,
                Grants: GovernedQueryStages.ToGrantRefs(decision),
                Source: new SourceExecutionStats(SourceComputeMs: 40, SourceCost: 1.5m, SourceCostUnit: "DBU", SourceQueryId: "q-1"));

            recorder.Record(fact, tenant, actor);
            scope.Complete(durationMs: 55, bytes: 4096, statusCode: 200);
        }

        var snap = aggregator.Swap();

        // Main series counts the user/table access ONCE.
        var userSeries = snap.Series.ShouldHaveSingleItem();
        userSeries.Value.Requests.ShouldBe(1);
        userSeries.Value.Queries.ShouldBe(1);
        userSeries.Value.Rows.ShouldBe(25);
        userSeries.Value.Bytes.ShouldBe(4096);
        userSeries.Value.DurationMs.ShouldBe(55);
        userSeries.Value.AnalysisCost.ShouldBe(7);
        userSeries.Value.Allow.ShouldBe(1);

        // Grant series counts EVERY contributing grant in FULL.
        snap.GrantSeries.Count.ShouldBe(2);
        foreach (var grantId in new[] { "grant-alpha", "grant-beta" })
        {
            var key = snap.GrantSeries.Keys.Single(k => k.GrantId == grantId);
            var counters = snap.GrantSeries[key];
            counters.Accesses.ShouldBe(1);
            counters.Queries.ShouldBe(1);
            counters.Rows.ShouldBe(25);
            counters.Bytes.ShouldBe(4096);
            counters.AnalysisCost.ShouldBe(7);
            counters.SourceComputeMs.ShouldBe(40);
        }
    }

    [Fact]
    public async Task Owner_access_attributed_to_owner_grant_id()
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var validFrom = now.AddDays(-5);
        var validUntil = now.AddDays(365);
        var aggregator = new UsageAggregator(timeProvider: new FixedTimeProvider(now));
        var recorder = new UsageRecorder(aggregator);
        var audit = new CapturingAuditWriter();

        var tenant = new TenantId("tenant-owner");
        var ownerSid = new Sid("S-1-5-21-DATA-OWNER");
        var table = new TableIdentifier("finance", "dbo", "ledger");
        const string ownerGrantId = "owner-grant-991a";

        var decision = TableAccessDecision.Allowed(
            table,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase),
            rowFilterSql: null,
            hasUnconstrainedColumnAllow: true) with
        {
            Grants = [new DecisionGrantRef(ownerGrantId, GrantSourceKind.Declarative, validFrom, validUntil)]
        };

        await GovernedQueryStages.RecordTableReadAsync(
            audit,
            tenant,
            ownerSid,
            table,
            allowed: true,
            GovernedQueryStages.DecisionDetails(decision),
            CancellationToken.None,
            usageRecorder: recorder,
            dataSource: "finance-pg",
            grants: decision.Grants);

        var snap = aggregator.Swap();
        snap.GrantSeries.Keys.ShouldContain(k => k.GrantId == ownerGrantId);
        snap.GrantSeries.Keys.ShouldNotContain(k => k.GrantId.StartsWith("basis:", StringComparison.OrdinalIgnoreCase));
        snap.GrantRefs.TryGetValue((tenant, ownerGrantId), out var storedRef).ShouldBeTrue();
        storedRef.GrantId.ShouldBe(ownerGrantId);
        storedRef.ValidFrom.ShouldBe(validFrom);
        storedRef.ValidUntil.ShouldBe(validUntil);
    }

    [Fact]
    public async Task Deny_fact_has_no_grants()
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var aggregator = new UsageAggregator(timeProvider: new FixedTimeProvider(now));
        var recorder = new UsageRecorder(aggregator);
        var audit = new CapturingAuditWriter();

        var tenant = new TenantId("tenant-deny");
        var actor = new Sid("S-1-5-21-DENIED");
        var table = new TableIdentifier("sales", "dbo", "secrets");

        // Even if a pre-failure decision previously held grants, a Deny audit/fact records zero grants.
        var staleGrants = new[]
        {
            new DecisionGrantRef("should-not-appear", GrantSourceKind.ConsentRequest, now.AddDays(-1), now.AddDays(10))
        };

        await GovernedQueryStages.RecordTableReadAsync(
            audit,
            tenant,
            actor,
            table,
            allowed: false,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["is_allowed"] = false },
            CancellationToken.None,
            usageRecorder: recorder,
            dataSource: "sales-pg",
            grants: staleGrants);

        recorder.Record(
            new UsageFact(table, "sales-pg", UsageOutcome.DenyRateLimit, 0, false, 15, [], default),
            tenant,
            actor);

        var snap = aggregator.Swap();
        snap.GrantSeries.ShouldBeEmpty();
        snap.GrantRefs.ShouldBeEmpty();

        var series = snap.Series.ShouldHaveSingleItem();
        series.Value.DenyAccess.ShouldBe(1);
        series.Value.DenyRateLimit.ShouldBe(1);
        series.Value.Allow.ShouldBe(0);

        var auditEntry = audit.Entries.ShouldHaveSingleItem();
        auditEntry.Decision.ShouldBe("DENY");
        using var doc = JsonDocument.Parse(auditEntry.DetailsJson);
        doc.RootElement.TryGetProperty("grants", out var grantsEl).ShouldBeTrue();
        grantsEl.ValueKind.ShouldBe(JsonValueKind.Array);
        grantsEl.GetArrayLength().ShouldBe(0);
    }
}
