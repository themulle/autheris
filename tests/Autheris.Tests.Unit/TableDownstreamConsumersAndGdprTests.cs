namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Linq;
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

public class TableDownstreamConsumersAndGdprTests
{
    private readonly IConsentRepository _consentRepo = Substitute.For<IConsentRepository>();
    private readonly IDataOwnershipRepository _ownershipRepo = Substitute.For<IDataOwnershipRepository>();
    private readonly IAuditLogRepository _auditRepo = Substitute.For<IAuditLogRepository>();
    private readonly ITableMetadataRepository _tableMetadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly LineageGraphStore _graphStore = new();
    private readonly LineageImpactAnalyzerService _sut;

    public TableDownstreamConsumersAndGdprTests()
    {
        _sut = new LineageImpactAnalyzerService(
            _consentRepo,
            _ownershipRepo,
            _graphStore,
            _auditRepo,
            _tableMetadataRepo,
            NullLogger<LineageImpactAnalyzerService>.Instance);
    }

    [Fact]
    public async Task GetTableConsumersAsync_WithDownstreamDashboardsAndHighVolume_CalculatesCriticalRiskAndMitigations()
    {
        var table = new TableIdentifier("sales", "dbo", "orders");
        var rootId = table.ToString();

        var ordersNode = new LineageNode(rootId, "orders", LineageNodeType.Table, ["dash_sales", "pipeline_etl"], "Sales Team", "sales-lead@corp.local");
        var dashNode = new LineageNode("dash_sales", "Executive Sales KPI Dashboard", LineageNodeType.Dashboard, [], "BI Team", "bi-admin@corp.local");
        var pipeNode = new LineageNode("pipeline_etl", "Sales Warehouse Daily ETL", LineageNodeType.Pipeline, [], "Data Eng", "dataeng@corp.local");

        _graphStore.UpdateGraph([ordersNode, dashNode, pipeNode]);

        var now = DateTimeOffset.UtcNow;
        var fakeLogs = new List<AuditLogEntry>
        {
            new() { OccurredAt = now.AddDays(-2), ActorSid = new Sid("S-1-5-21-USER1"), TargetTable = rootId, Decision = "ALLOW", EventType = "TABLE_QUERY" },
            new() { OccurredAt = now.AddDays(-1), ActorSid = new Sid("S-1-5-21-USER2"), TargetTable = rootId, Decision = "ALLOW", EventType = "TABLE_QUERY" },
            new() { OccurredAt = now.AddHours(-3), ActorSid = new Sid("SP-ETL-SERVICE-PRINCIPAL"), TargetTable = rootId, Decision = "ALLOW", EventType = "TABLE_QUERY" },
            new() { OccurredAt = now.AddHours(-1), ActorSid = new Sid("S-1-5-21-USER1"), TargetTable = rootId, Decision = "ALLOW", EventType = "TABLE_QUERY" }
        };

        _auditRepo.QueryAuditLogsAsync(
            targetTable: rootId,
            actorSid: null,
            since: Arg.Any<DateTimeOffset>(),
            limit: Arg.Any<int>(),
            tenantId: Arg.Any<TenantId?>(),
            ct: Arg.Any<CancellationToken>())
            .Returns(fakeLogs);

        // SEC M-15: Runtime-Consumer (Zugriffsprotokoll) nur für Owner/Delegierte, GovernanceAdmin und PrivacyAdmin.
        var adminContext = new CallerSecurityContext(
            new Sid("S-1-5-21-ADMIN"),
            [],
            ["GovernanceAdmin"],
            new TenantId("tenant-1"),
            IsGovernanceAdmin: true,
            IsClusterAdmin: false);

        var report = await _sut.GetTableConsumersAsync(table, timeWindowDays: 30, callerContext: adminContext);

        report.ShouldNotBeNull();
        report.Table.ShouldBe("sales.dbo.orders");
        report.TotalDownstreamCount.ShouldBe(2); // Dashboard + Pipeline
        report.ActiveReadersCount.ShouldBe(3); // USER1, USER2, SP-ETL-SERVICE-PRINCIPAL
        report.BreakingChangeRisk.ShouldBe("CRITICAL"); // Dashboards + >= 3 active readers

        report.DownstreamConsumers.ShouldContain(d => d.Type == LineageNodeType.Dashboard && d.Name == "Executive Sales KPI Dashboard");
        report.DownstreamConsumers.ShouldContain(d => d.Type == LineageNodeType.Pipeline && d.Name == "Sales Warehouse Daily ETL");

        // Admin sees unmasked owner email
        var dash = report.DownstreamConsumers.First(d => d.Id == "dash_sales");
        dash.OwnerEmail.ShouldBe("bi-admin@corp.local");

        // Runtime consumers categorized
        var spConsumer = report.RuntimeConsumers.FirstOrDefault(c => c.ActorSid == "SP-ETL-SERVICE-PRINCIPAL");
        spConsumer.ShouldNotBeNull();
        spConsumer.ClientType.ShouldBe("ServicePrincipal");
        spConsumer.QueryCount.ShouldBe(1);

        var user1Consumer = report.RuntimeConsumers.FirstOrDefault(c => c.ActorSid == "S-1-5-21-USER1");
        user1Consumer.ShouldNotBeNull();
        user1Consumer.QueryCount.ShouldBe(2);

        // Mitigations contain dashboard and pipeline warnings
        report.RecommendedMitigations.ShouldContain(m => m.Contains("Executive Sales KPI Dashboard"));
        report.RecommendedMitigations.ShouldContain(m => m.Contains("Sales Warehouse Daily ETL"));
        report.RecommendedMitigations.ShouldContain(m => m.Contains("deprecation period"));
    }

    [Fact]
    public async Task GetTableConsumersAsync_CallerIsNonAdmin_MasksOwnerEmails()
    {
        var table = new TableIdentifier("hr", "dbo", "employees");
        var rootId = table.ToString();

        var empNode = new LineageNode(rootId, "employees", LineageNodeType.Table, ["dash_headcount"], "HR Team", "hr@corp.local");
        var dashNode = new LineageNode("dash_headcount", "Headcount Dashboard", LineageNodeType.Dashboard, [], "HR Analytics", "hr-analytics@corp.local");

        _graphStore.UpdateGraph([empNode, dashNode]);

        _auditRepo.QueryAuditLogsAsync(
            targetTable: rootId,
            actorSid: null,
            since: Arg.Any<DateTimeOffset>(),
            limit: Arg.Any<int>(),
            ct: Arg.Any<CancellationToken>())
            .Returns(new List<AuditLogEntry>());

        var nonAdmin = new CallerSecurityContext(
            new Sid("S-1-5-21-REGULAR-USER"),
            [],
            ["Analyst"],
            new TenantId("tenant-1"),
            IsGovernanceAdmin: false,
            IsClusterAdmin: false);

        _ownershipRepo.IsAuthorizedApproverForTableAsync(table, nonAdmin.UserSid, Arg.Any<CancellationToken>())
            .Returns(false);

        var report = await _sut.GetTableConsumersAsync(table, timeWindowDays: 30, callerContext: nonAdmin);

        var dash = report.DownstreamConsumers.ShouldHaveSingleItem();
        dash.OwnerTeam.ShouldBe("HR Analytics");
        dash.OwnerEmail.ShouldBeNull(); // Masked!
    }

    [Fact]
    public async Task GetGdprDataDisclosureReportAsync_WithGdprArticle9Table_GeneratesArticle15Report()
    {
        var table = new TableIdentifier("healthcare", "dbo", "patient_diagnoses");
        var rootId = table.ToString();

        var meta = new TableMetadata
        {
            Identifier = table,
            Table = new Table
            {
                TableName = "patient_diagnoses",
                SchemaName = "dbo",
                Sensitivity = "GDPR_ARTICLE_9",
                RequiresFourEyes = true
            },
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["diagnosis_code"] = new MaskingRule { RuleType = "REDACT" }
            }
        };

        _tableMetadataRepo.GetTableMetadataAsync(table, Arg.Any<CancellationToken>()).Returns(meta);

        var now = DateTimeOffset.UtcNow;
        var auditLogs = new List<AuditLogEntry>
        {
            new()
            {
                OccurredAt = now.AddDays(-10),
                ActorSid = new Sid("S-1-5-21-DOCTOR-A"),
                TargetTable = rootId,
                TargetColumn = "diagnosis_code",
                Decision = "ALLOW",
                EventType = "TABLE_QUERY"
            },
            new()
            {
                OccurredAt = now.AddDays(-2),
                ActorSid = new Sid("svc_ehr_sync_daemon"),
                TargetTable = rootId,
                TargetColumn = "patient_id",
                Decision = "ALLOW",
                EventType = "TABLE_QUERY"
            }
        };

        _auditRepo.QueryAuditLogsAsync(
            targetTable: rootId,
            actorSid: null,
            since: Arg.Any<DateTimeOffset>(),
            limit: Arg.Any<int>(),
            tenantId: Arg.Any<TenantId?>(),
            ct: Arg.Any<CancellationToken>())
            .Returns(auditLogs);

        var report = await _sut.GetGdprDataDisclosureReportAsync(table, subjectSid: null, timeWindowDays: 365);

        report.ShouldNotBeNull();
        report.TargetTable.ShouldBe("healthcare.dbo.patient_diagnoses");
        report.TotalAccessEvents.ShouldBe(2);
        report.DisclosedRecipients.Count.ShouldBe(2);

        // Sensitivity category classified as GDPR Art. 9
        report.SensitivityCategories.ShouldContain(s => s.Contains("GDPR_ARTICLE_9"));

        // Recipient classification
        var svcRecipient = report.DisclosedRecipients.First(r => r.RecipientSid == "svc_ehr_sync_daemon");
        svcRecipient.RecipientCategory.ShouldBe("ServicePrincipal");

        var docRecipient = report.DisclosedRecipients.First(r => r.RecipientSid == "S-1-5-21-DOCTOR-A");
        docRecipient.RecipientCategory.ShouldBe("InteractiveUser");
        docRecipient.AccessedColumns.ShouldContain("diagnosis_code");
        docRecipient.MaskingRuleApplied.ShouldBe("diagnosis_code: REDACT");

        // Legal basis notice
        report.LegalBasisNotice.ShouldContain("Art. 15 Abs. 1 Bst. c DSGVO");
    }
}
