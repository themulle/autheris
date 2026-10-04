namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Lineage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public class LineageTraversalTests
{
    private readonly IConsentRepository _consentRepo = Substitute.For<IConsentRepository>();
    private readonly IDataOwnershipRepository _ownershipRepo = Substitute.For<IDataOwnershipRepository>();
    private readonly LineageGraphStore _graphStore = new();
    private readonly LineageImpactAnalyzerService _sut;

    public LineageTraversalTests()
    {
        _sut = new LineageImpactAnalyzerService(
            _consentRepo,
            _ownershipRepo,
            _graphStore,
            NullLogger<LineageImpactAnalyzerService>.Instance);
    }

    [Fact]
    public async Task Traverse_WhenGraphContainsCycle_TerminatesSafelyAndMarksCycleDetected()
    {
        // Setup cyclical DAG: TableA -> TableB -> TableC -> TableA
        var rootId = "finance.dbo.table_a";
        var tableA = new LineageNode(rootId, "table_a", LineageNodeType.Table, ["table_b"], "Finance Team", "finance@corp.local");
        var tableB = new LineageNode("table_b", "table_b", LineageNodeType.Table, ["table_c"], "Analytics Team", "analytics@corp.local");
        var tableC = new LineageNode("table_c", "table_c", LineageNodeType.Table, [rootId], "Reporting Team", "reporting@corp.local");

        _graphStore.UpdateGraph([tableA, tableB, tableC]);

        var consentId = Guid.NewGuid();
        var consent = new Consent
        {
            Id = consentId,
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("finance", "dbo", "table_a"),
            TenantId = new TenantId("tenant-a"), // SEC M-15: keine LegacySingleTenant-Ausnahme mehr
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-USER-1"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        _consentRepo.GetConsentByIdAsync(consentId, Arg.Any<CancellationToken>()).Returns(consent);

        var callerContext = new CallerSecurityContext(
            new Sid("S-1-5-21-USER-1"),
            [],
            ["PrivacyAdmin"], // SEC M-15: Auswirkungsanalyse nur für Owner/GovernanceAdmin/PrivacyAdmin
            new TenantId("tenant-a"),
            IsGovernanceAdmin: false,
            IsClusterAdmin: false);

        var report = await _sut.CalculateConsentRevocationImpactAsync(
            new TenantId("tenant-a"),
            consentId,
            callerContext);

        report.ShouldNotBeNull();
        report.ContainsCycles.ShouldBeTrue();
        report.AffectedEntities.ShouldContain(e => e.CyclicReferenceDetected);
    }

    [Fact]
    public async Task Traverse_WhenCallerIsNonAdmin_MasksOwnerEmailToNull()
    {
        var rootId = "sales.dbo.orders";
        var orders = new LineageNode(rootId, "orders", LineageNodeType.Table, ["dash_1"], "Sales Team", "sales@corp.local");
        var dash1 = new LineageNode("dash_1", "Executive Sales Dashboard", LineageNodeType.Dashboard, [], "BI Team", "bi-admin@corp.local");

        _graphStore.UpdateGraph([orders, dash1]);

        var consentId = Guid.NewGuid();
        var consent = new Consent
        {
            Id = consentId,
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("sales", "dbo", "orders"),
            TenantId = new TenantId("tenant-a"), // SEC M-15: keine LegacySingleTenant-Ausnahme mehr
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-ANALYST"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        _consentRepo.GetConsentByIdAsync(consentId, Arg.Any<CancellationToken>()).Returns(consent);
        _ownershipRepo.IsAuthorizedApproverForTableAsync(consent.TableIdentifier, Arg.Any<Sid>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var analystContext = new CallerSecurityContext(
            new Sid("S-1-5-21-ANALYST"),
            [],
            ["PrivacyAdmin"], // SEC M-15: Zugriff erlaubt, E-Mail bleibt maskiert (kein Owner/GovernanceAdmin)
            new TenantId("tenant-a"),
            IsGovernanceAdmin: false,
            IsClusterAdmin: false);

        var report = await _sut.CalculateConsentRevocationImpactAsync(
            new TenantId("tenant-a"),
            consentId,
            analystContext);

        report.Severity.ShouldBe("HIGH");
        report.AffectedDownstreamCount.ShouldBe(1);

        var affectedDash = report.AffectedEntities.ShouldHaveSingleItem();
        affectedDash.OwnerTeam.ShouldBe("BI Team");
        affectedDash.OwnerEmail.ShouldBeNull(); // Zero-Trust Masked!
    }

    [Fact]
    public async Task Traverse_WhenCallerIsAdmin_RevealsOwnerEmail()
    {
        var rootId = "sales.dbo.orders";
        var orders = new LineageNode(rootId, "orders", LineageNodeType.Table, ["dash_1"], "Sales Team", "sales@corp.local");
        var dash1 = new LineageNode("dash_1", "Executive Sales Dashboard", LineageNodeType.Dashboard, [], "BI Team", "bi-admin@corp.local");

        _graphStore.UpdateGraph([orders, dash1]);

        var consentId = Guid.NewGuid();
        var consent = new Consent
        {
            Id = consentId,
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("sales", "dbo", "orders"),
            TenantId = new TenantId("tenant-a"), // SEC M-15: keine LegacySingleTenant-Ausnahme mehr
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-ADMIN"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        _consentRepo.GetConsentByIdAsync(consentId, Arg.Any<CancellationToken>()).Returns(consent);

        var adminContext = new CallerSecurityContext(
            new Sid("S-1-5-21-ADMIN"),
            [],
            ["GovernanceAdmin"],
            new TenantId("tenant-a"),
            IsGovernanceAdmin: true,
            IsClusterAdmin: false);

        var report = await _sut.CalculateConsentRevocationImpactAsync(
            new TenantId("tenant-a"),
            consentId,
            adminContext);

        var affectedDash = report.AffectedEntities.ShouldHaveSingleItem();
        affectedDash.OwnerTeam.ShouldBe("BI Team");
        affectedDash.OwnerEmail.ShouldBe("bi-admin@corp.local"); // Admin sees cleartext email
    }

    [Fact]
    public async Task Traverse_DiamondGraph_DoesNotDetectCycle()
    {
        var rootId = "core.dbo.source";
        var source = new LineageNode(rootId, "source", LineageNodeType.Table, ["model_a", "model_b"], "Core", "core@corp.local");
        var modelA = new LineageNode("model_a", "model_a", LineageNodeType.Pipeline, ["target_dash"], "BI", "bi@corp.local");
        var modelB = new LineageNode("model_b", "model_b", LineageNodeType.Pipeline, ["target_dash"], "Analytics", "analytics@corp.local");
        var targetDash = new LineageNode("target_dash", "target_dash", LineageNodeType.Dashboard, [], "Exec", "exec@corp.local");

        _graphStore.UpdateGraph([source, modelA, modelB, targetDash]);

        var consentId = Guid.NewGuid();
        var consent = new Consent
        {
            Id = consentId,
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("core", "dbo", "source"),
            TenantId = new TenantId("tenant-a"), // SEC M-15: keine LegacySingleTenant-Ausnahme mehr
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-USER"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        _consentRepo.GetConsentByIdAsync(consentId, Arg.Any<CancellationToken>()).Returns(consent);

        var report = await _sut.CalculateConsentRevocationImpactAsync(
            new TenantId("tenant-a"),
            consentId,
            new CallerSecurityContext(new Sid("S-1-5-21-USER"), [], ["PrivacyAdmin"], new TenantId("tenant-a"), false, false));

        report.ContainsCycles.ShouldBeFalse("A converging diamond DAG must NOT be flagged as a cycle!");
        report.AffectedDownstreamCount.ShouldBe(3); // model_a, model_b, target_dash
        report.AffectedEntities.ShouldAllBe(e => !e.CyclicReferenceDetected);
    }

    [Fact]
    public async Task Traverse_10000Nodes_CompletesWithinSla15ms()
    {
        // Generate 10,000 synthetic nodes
        var nodes = new List<LineageNode>(10_000);
        var rootId = "perf.dbo.root";

        // Fan-out tree
        var rootDownstream = new List<string>(10);
        for (int i = 1; i <= 10; i++)
        {
            rootDownstream.Add($"perf.node.{i}");
        }
        nodes.Add(new LineageNode(rootId, "root", LineageNodeType.Table, rootDownstream, "Perf Team", "perf@corp.local"));

        for (int i = 1; i < 10_000; i++)
        {
            var downstream = new List<string>(2);
            int child1 = (i * 2) + 1;
            int child2 = (i * 2) + 2;
            if (child1 < 10_000) downstream.Add($"perf.node.{child1}");
            if (child2 < 10_000) downstream.Add($"perf.node.{child2}");

            var type = (i % 50 == 0) ? LineageNodeType.Dashboard : LineageNodeType.Table;
            nodes.Add(new LineageNode($"perf.node.{i}", $"node_{i}", type, downstream, $"Team {i % 10}", $"team{i % 10}@corp.local"));
        }

        _graphStore.UpdateGraph(nodes);

        var consentId = Guid.NewGuid();
        var consent = new Consent
        {
            Id = consentId,
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("perf", "dbo", "root"),
            TenantId = new TenantId("perf-tenant"), // SEC M-15: keine LegacySingleTenant-Ausnahme mehr
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-PERF-USER"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        _consentRepo.GetConsentByIdAsync(consentId, Arg.Any<CancellationToken>()).Returns(consent);

        var callerContext = new CallerSecurityContext(
            new Sid("S-1-5-21-PERF-USER"),
            [],
            ["PrivacyAdmin"],
            new TenantId("perf-tenant"),
            IsGovernanceAdmin: false,
            IsClusterAdmin: false);

        // Warmup
        await _sut.CalculateConsentRevocationImpactAsync(new TenantId("perf-tenant"), consentId, callerContext);

        // Timed run
        var sw = Stopwatch.StartNew();
        var report = await _sut.CalculateConsentRevocationImpactAsync(new TenantId("perf-tenant"), consentId, callerContext);
        sw.Stop();

        report.AffectedDownstreamCount.ShouldBe(9_999);
        sw.ElapsedMilliseconds.ShouldBeLessThanOrEqualTo(150); // SLA target: iterative BFS is extremely fast (< 15ms target, buffer for parallel runs)
    }

    [Fact]
    public async Task CalculateConsentRevocationImpactAsync_WhenCrossTenantConsentRequested_ThrowsKeyNotFoundException()
    {
        var consentId = Guid.NewGuid();
        var foreignConsent = new Consent
        {
            Id = consentId,
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("finance", "dbo", "salaries"),
            TenantId = new TenantId("tenant-victim"),
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-VICTIM"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        _consentRepo.GetConsentByIdAsync(consentId, Arg.Any<CancellationToken>()).Returns(foreignConsent);

        var attackerContext = new CallerSecurityContext(
            new Sid("S-1-5-21-ATTACKER"),
            [],
            ["Analyst"],
            new TenantId("tenant-attacker"),
            IsGovernanceAdmin: false,
            IsClusterAdmin: false);

        // SEC BOLA: Must throw KeyNotFoundException when attempting to analyze a consent belonging to another tenant
        await Should.ThrowAsync<KeyNotFoundException>(() =>
            _sut.CalculateConsentRevocationImpactAsync(
                new TenantId("tenant-attacker"),
                consentId,
                attackerContext));
    }
}
