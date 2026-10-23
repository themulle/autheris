namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class TableConsumersDualTierSecurityTests
{
    private readonly IConsentRepository _consentRepo = Substitute.For<IConsentRepository>();
    private readonly IDataOwnershipRepository _ownershipRepo = Substitute.For<IDataOwnershipRepository>();
    private readonly ILineageGraphStore _graphStore = Substitute.For<ILineageGraphStore>();
    private readonly IAuditLogRepository _auditRepo = Substitute.For<IAuditLogRepository>();
    private readonly ITableMetadataRepository _tableMetadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IConsumerAggregationRepository _consumerAggregationRepo = Substitute.For<IConsumerAggregationRepository>();

    private LineageImpactAnalyzerService CreateService()
    {
        return new LineageImpactAnalyzerService(
            _consentRepo,
            _ownershipRepo,
            _graphStore,
            _auditRepo,
            _tableMetadataRepo,
            NullLogger<LineageImpactAnalyzerService>.Instance,
            _consumerAggregationRepo);
    }

    [Fact]
    public async Task T_SEC_AUD114_03_DualTierGate_NonOwner_SuppressesPersonalSids()
    {
        // Enforces SEC-AUD114-02: Non-privileged caller receives aggregated view with zero personal SIDs.
        var table = new TableIdentifier("sales", "dbo", "orders");
        var caller = new CallerSecurityContext(
            UserSid: new Sid("S-1-5-21-CONSUMER"),
            GroupSids: Array.Empty<Sid>(),
            Roles: new[] { "Analyst" },
            Tenant: new TenantId("tenant-1"),
            IsGovernanceAdmin: false,
            IsClusterAdmin: false
        );

        // Caller is NOT owner or approver
        _ownershipRepo.IsAuthorizedApproverForTableAsync(table, caller.UserSid, Arg.Any<CancellationToken>())
            .Returns(false);

        // Pre-aggregated summaries exist
        var summaries = new List<ConsumerAppDailySummary>
        {
            new("powerbi", "2026-10-18", 100, 5000, 100, new RejectionBreakdown(), ["1.0"], ["GetOrders"], DistinctActors: 5),
            new("niche-app", "2026-10-18", 10, 50, 10, new RejectionBreakdown(), ["1.0"], ["PrivateQuery"], DistinctActors: 1)
        };
        _consumerAggregationRepo.QueryConsumerAppDailyAsync(table, caller.Tenant, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(summaries);

        var actorCounts = new Dictionary<string, int>
        {
            ["powerbi"] = 5,
            ["niche-app"] = 1
        };
        _consumerAggregationRepo.QueryDistinctActorCountsAsync(table, caller.Tenant, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(actorCounts);

        var service = CreateService();
        var report = await service.GetTableConsumersAsync(table, 30, caller);

        // Personal SIDs in RuntimeConsumers must be completely suppressed for non-owner
        report.RuntimeConsumers.ShouldBeEmpty();

        // ApplicationUsages must be present but personal ActorSids and individual query names must be null/empty
        report.ApplicationUsages.ShouldNotBeNull();
        foreach (var usage in report.ApplicationUsages)
        {
            usage.ActorSids.ShouldBeNull();
            usage.QueryNames.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task T_SEC_AUD114_04_DualTierGate_DataOwner_ExposesPersonalBreakdown()
    {
        // Enforces SEC-AUD114-02: Verified Data Owner receives detailed actor SIDs and query names.
        var table = new TableIdentifier("sales", "dbo", "orders");
        var ownerSid = new Sid("S-1-5-21-DATAOWNER");
        var caller = new CallerSecurityContext(
            UserSid: ownerSid,
            GroupSids: Array.Empty<Sid>(),
            Roles: new[] { "DataOwner" },
            Tenant: new TenantId("tenant-1"),
            IsGovernanceAdmin: false,
            IsClusterAdmin: false
        );

        // Caller IS verified table approver
        _ownershipRepo.IsAuthorizedApproverForTableAsync(table, ownerSid, Arg.Any<CancellationToken>())
            .Returns(true);

        var auditEntries = new List<AuditLogEntry>
        {
            new()
            {
                TenantId = caller.Tenant,
                EventType = "TABLE_QUERY",
                ActorSid = new Sid("S-1-5-21-ALICE"),
                TargetTable = table.ToString(),
                Decision = "ALLOW",
                OccurredAt = DateTimeOffset.UtcNow
            }
        };
        _auditRepo.QueryAuditLogsAsync(table.ToString(), Arg.Any<Sid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(auditEntries);

        var summaries = new List<ConsumerAppDailySummary>
        {
            new("powerbi", "2026-10-18", 100, 5000, 100, new RejectionBreakdown(), ["1.0"], ["SelectAllOrders"], DistinctActors: 5)
        };
        _consumerAggregationRepo.QueryConsumerAppDailyAsync(table, caller.Tenant, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(summaries);

        var service = CreateService();
        var report = await service.GetTableConsumersAsync(table, 30, caller);

        // Data Owner sees runtime consumers personal breakdown
        report.RuntimeConsumers.ShouldNotBeEmpty();
        report.RuntimeConsumers.ShouldContain(r => r.ActorSid == "S-1-5-21-ALICE");

        // ApplicationUsages includes query names and actor breakdown
        report.ApplicationUsages.ShouldNotBeNull();
        var app = report.ApplicationUsages.First(a => a.Application == "powerbi");
        app.QueryNames.ShouldContain("SelectAllOrders");
        app.ActorSids.ShouldNotBeNull();
        app.ActorSids.ShouldContain("S-1-5-21-ALICE");
    }

    [Fact]
    public void T_SEC_AUD114_05_kAnonymity_ActorsBelowThree_FoldsToOther()
    {
        // Enforces SEC-AUD114-03: Applications with < 3 distinct actors are folded into "Other / Aggregated"
        var summaries = new List<ConsumerAppDailySummary>
        {
            new("app-large", "2026-10-18", 500, 20000, 500, new RejectionBreakdown(1, 0, 0, 0), ["v1"], ["Q1"], DistinctActors: 10),
            new("app-niche-1", "2026-10-18", 20, 100, 20, new RejectionBreakdown(0, 1, 0, 0), ["v1"], ["Q2"], DistinctActors: 1)
            {
                ActorKeys = new HashSet<string> { "h1" }
            },
            new("app-niche-2", "2026-10-18", 30, 150, 30, new RejectionBreakdown(0, 0, 1, 0), ["v2"], ["Q3"], DistinctActors: 2)
            {
                ActorKeys = new HashSet<string> { "h2", "h3" }
            }
        };

        var (results, suppressed) = ConsumerFolding.Fold(summaries, minActors: 3);

        // app-large has 10 actors (>= 3) -> kept
        results.ShouldContain(r => r.Application == "app-large" && r.Calls == 500);

        // app-niche-1 and app-niche-2 must NOT appear individually
        results.ShouldNotContain(r => r.Application == "app-niche-1");
        results.ShouldNotContain(r => r.Application == "app-niche-2");

        // Folded into Other / Aggregated bucket: combined actors = 1 + 2 = 3 (>= 3), so counts are not suppressed
        var other = results.FirstOrDefault(r => r.Application == ConsumerFolding.DefaultOtherBucketName);
        other.ShouldNotBeNull();
        other.Calls.ShouldBe(50); // 20 + 30
        other.RowsReturned.ShouldBe(250); // 100 + 150
        other.Rejections.RateLimit.ShouldBe(1);
        other.Rejections.Cost.ShouldBe(1);
        other.DistinctActors.ShouldBe(3);
        suppressed.ShouldBeFalse();
    }

    [Fact]
    public void T_SEC_AUD114_06_kAnonymity_OtherBucketBelowThree_SuppressesCounts()
    {
        // Enforces SEC-AUD114-03: Combined other bucket with < 3 actors suppresses counts entirely
        var summaries = new List<ConsumerAppDailySummary>
        {
            new("app-single-user", "2026-10-18", 50, 1000, 50, new RejectionBreakdown(2, 0, 0, 0), ["v1"], ["Q1"], DistinctActors: 1)
            {
                ActorKeys = new HashSet<string> { "h1" }
            }
        };

        var (results, suppressed) = ConsumerFolding.Fold(summaries, minActors: 3);

        // app-single-user must be folded into Other / Aggregated
        results.ShouldNotContain(r => r.Application == "app-single-user");

        // Combined bucket has only 1 distinct actor (< 3), so its counts must be suppressed
        var other = results.FirstOrDefault(r => r.Application == ConsumerFolding.DefaultOtherBucketName);
        other.ShouldNotBeNull();
        other.Calls.ShouldBe(0);
        other.RowsReturned.ShouldBe(0);
        other.RowsMeasuredCalls.ShouldBe(0);
        other.Rejections.Total.ShouldBe(0);
        suppressed.ShouldBeTrue();
    }
}
