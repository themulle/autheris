namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class PolicySimulationAndSunsettingTests
{
    [Fact]
    public async Task PolicySimulation_ShouldDetectNewlyDeniedAndNewlyAllowedRequests()
    {
        // Arrange
        var auditRepo = Substitute.For<IAuditLogRepository>();
        var userAlice = new Sid("S-1-5-21-ALICE");
        var userBob = new Sid("S-1-5-21-BOB");

        var auditEntries = new List<AuditLogEntry>
        {
            new()
            {
                Id = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                ActorSid = userAlice,
                TargetTable = "Customers",
                EventType = "read",
                Decision = "ALLOW"
            },
            new()
            {
                Id = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                ActorSid = userBob,
                TargetTable = "Customers",
                EventType = "read",
                Decision = "DENY"
            }
        };

        auditRepo.QueryAuditLogsAsync(
            Arg.Any<string?>(),
            Arg.Any<Sid?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(),
            Arg.Any<TenantId?>(),
            Arg.Any<CancellationToken>()
        ).Returns(auditEntries);

        var simulationService = new PolicySimulationService(auditRepo);

        // Bob gets allowed, Alice gets denied in the draft policy
        var draftCsv = @"
p, S-1-5-21-BOB, tenant1, Customers, read, true, allow
";

        var request = new PolicySimulationRequest(
            DraftPolicyCsv: draftCsv,
            Tenant: new TenantId("tenant1"),
            TargetTable: "Customers",
            Limit: 100
        );

        // Act
        var result = await simulationService.SimulateAsync(request, new TenantId("tenant1"));

        // Assert
        result.TotalEvaluatedLogs.ShouldBe(2);
        result.AllowedInBaseline.ShouldBe(1);
        result.DeniedInBaseline.ShouldBe(1);
        result.AllowedInSimulation.ShouldBe(1);
        result.DeniedInSimulation.ShouldBe(1);
        result.NewlyDeniedCount.ShouldBe(1);
        result.NewlyAllowedCount.ShouldBe(1);
        result.ImpactPercentage.ShouldBe(100.0);

        result.Differences.Count.ShouldBe(2);
        result.Differences.ShouldContain(d => d.ActorSid == userAlice && d.HistoricalDecision == "ALLOW" && d.SimulatedDecision == "DENY");
        result.Differences.ShouldContain(d => d.ActorSid == userBob && d.HistoricalDecision == "DENY" && d.SimulatedDecision == "ALLOW");

        result.PerTableSummaries.ShouldContainKey("Customers");
        var tableSummary = result.PerTableSummaries["Customers"];
        tableSummary.EvaluatedCount.ShouldBe(2);
        tableSummary.ChangedCount.ShouldBe(2);
    }

    [Fact]
    public async Task PolicySimulation_ShouldRejectDangerousTokensInDraftPolicy()
    {
        // Arrange
        var auditRepo = Substitute.For<IAuditLogRepository>();
        var simulationService = new PolicySimulationService(auditRepo);

        var maliciousDraftCsv = @"
p, S-1-5-21-HACKER, default, *, read, System.IO.File.ReadAllText('/etc/passwd') != '', allow
";

        var request = new PolicySimulationRequest(DraftPolicyCsv: maliciousDraftCsv);

        // Act & Assert
        var ex = await Should.ThrowAsync<ArgumentException>(() => simulationService.SimulateAsync(request, new TenantId("default")));
        ex.Message.ShouldContain("Security validation error");
        ex.Message.ShouldContain("System.");
    }

    [Fact]
    public async Task SchemaSunsetting_ShouldCorrectlyProgressThroughAllPhases()
    {
        // Arrange
        var sunsettingService = new SchemaSunsettingService();

        var baseTime = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var rule = new FieldSunsettingRule(
            Id: Guid.NewGuid(),
            TargetTable: "Users",
            FieldName: "legacy_phone",
            DeprecatedAt: baseTime.AddDays(10), // June 11
            SunsetAt: baseTime.AddDays(30),     // July 1
            ReplacementField: "mobile_phone_e164",
            DeprecationReason: "Migrating to E.164 international standard",
            BrownoutWindow: TimeSpan.FromDays(7), // Brownout starts June 24
            BrownoutPercentage: 1.0,               // 100% trigger for testing
            BrownoutLatencyMs: 250,
            BrownoutErrorCode: 426
        );

        await sunsettingService.RegisterRuleAsync(rule);

        // 1. Phase: Active (Before Deprecation)
        var activeEval = await sunsettingService.EvaluateFieldAsync("Users", "legacy_phone", baseTime.AddDays(5));
        activeEval.ShouldBeNull();

        // 2. Phase: Warning (Between Deprecation and Brownout)
        var warningDate = baseTime.AddDays(15);
        var warningEval = await sunsettingService.EvaluateFieldAsync("Users", "legacy_phone", warningDate);
        warningEval.ShouldNotBeNull();
        warningEval.Phase.ShouldBe(SunsettingPhase.Warning);
        warningEval.IsHardSunsetBlocked.ShouldBeFalse();
        warningEval.ShouldRejectWith426.ShouldBeFalse();
        warningEval.ShouldInjectSyntheticLatency.ShouldBeFalse();
        warningEval.DeprecationNotice.ShouldContain("deprecated");
        warningEval.DeprecationNotice.ShouldContain("mobile_phone_e164");
        warningEval.HttpSunsetHeader.ShouldNotBeNullOrWhiteSpace();

        // 3. Phase: Brownout (Chaos Testing window)
        var brownoutDate = baseTime.AddDays(25);
        var brownoutEval = await sunsettingService.EvaluateFieldAsync("Users", "legacy_phone", brownoutDate);
        brownoutEval.ShouldNotBeNull();
        brownoutEval.Phase.ShouldBe(SunsettingPhase.Brownout);
        brownoutEval.ShouldRejectWith426.ShouldBeTrue();
        brownoutEval.ShouldInjectSyntheticLatency.ShouldBeTrue();
        brownoutEval.SyntheticLatencyMs.ShouldBe(250);
        brownoutEval.DeprecationNotice.ShouldContain("brownout");

        // 4. Phase: Hard Sunset (After SunsetAt)
        var hardSunsetDate = baseTime.AddDays(31);
        var hardSunsetEval = await sunsettingService.EvaluateFieldAsync("Users", "legacy_phone", hardSunsetDate);
        hardSunsetEval.ShouldNotBeNull();
        hardSunsetEval.Phase.ShouldBe(SunsettingPhase.HardSunset);
        hardSunsetEval.IsHardSunsetBlocked.ShouldBeTrue();
        hardSunsetEval.DeprecationNotice.ShouldContain("permanently decommissioned");
        hardSunsetEval.DeprecationNotice.ShouldContain("mobile_phone_e164");
    }

    [Fact]
    public async Task SchemaSunsetting_ShouldManageRulesLifecycle()
    {
        // Arrange
        var sunsettingService = new SchemaSunsettingService();
        var ruleId = Guid.NewGuid();
        var rule = new FieldSunsettingRule(
            Id: ruleId,
            TargetTable: "Invoices",
            FieldName: "tax_code",
            DeprecatedAt: DateTimeOffset.UtcNow.AddDays(-5),
            SunsetAt: DateTimeOffset.UtcNow.AddDays(20)
        );

        // Act
        await sunsettingService.RegisterRuleAsync(rule);
        var rules = await sunsettingService.GetRulesAsync();
        var fetched = await sunsettingService.GetRuleAsync("Invoices", "tax_code");

        // Assert
        rules.Count.ShouldBe(1);
        fetched.ShouldNotBeNull();
        fetched.FieldName.ShouldBe("tax_code");

        // Remove
        var removed = await sunsettingService.RemoveRuleAsync(ruleId);
        removed.ShouldBeTrue();
        var afterRemove = await sunsettingService.GetRulesAsync();
        afterRemove.ShouldBeEmpty();
    }
}
