namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Docs.Interfaces;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class Issue271SecurityAuditVerificationTests
{
    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static McpSessionContext CreateSession(string sessionId, string userId, string tenantId, IReadOnlyList<string>? roles = null)
    {
        return new McpSessionContext(
            SessionId: sessionId,
            ServicePrincipalId: userId,
            TenantId: tenantId,
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow,
            UserSid: userId,
            Roles: roles ?? Array.Empty<string>());
    }

    [Fact]
    public void SEC_TEST_271_01_DockerPublish_WorkflowConditionGuard()
    {
        var root = FindRepositoryRoot();
        var workflowPath = Path.Combine(root, ".github/workflows/docker-publish.yml");
        File.Exists(workflowPath).ShouldBeTrue();

        var content = File.ReadAllText(workflowPath);
        content.ShouldContain("head_repository.full_name == github.repository");
        content.ShouldContain("head_branch == 'main'");
        content.ShouldContain("workflow_run.event == 'push'");
    }

    [Fact]
    public async Task SEC_TEST_271_02_DeclarativeConsent_SelfGrantProhibited()
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var auditRepo = Substitute.For<IAuditLogRepository>();
        var grantPolicy = Substitute.For<IGrantPolicy>();

        var service = new DeclarativeConsentApplyService(metaRepo, consentRepo, auditRepo, grantPolicy);

        var tableId = new TableIdentifier("sales", "dbo", "orders");
        var actorSid = "S-1-5-21-ATTACKER";

        metaRepo.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>())
            .Returns(new TableMetadata
            {
                Identifier = tableId,
                Table = new Table { Id = Guid.NewGuid(), IsActive = true, SchemaName = "dbo", TableName = "orders" },
                Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
            });

        var manifest = new DeclarativeConsentManifest(
            Version: "1.0",
            Policies:
            [
                new DeclarativeConsentPolicy(
                    Domain: "sales",
                    Schema: "dbo",
                    TableName: "orders",
                    GranteeType: "User",
                    GranteeRef: actorSid, // Self-grant attempt
                    Effect: "Allow",
                    ValidUntil: DateTimeOffset.UtcNow.AddDays(30))
            ]);

        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, actorSid),
            new Claim(ClaimTypes.Role, "GOVERNANCE_ADMIN")
        ], "TestAuth");
        var caller = new ClaimsPrincipal(identity);

        var ex = await Should.ThrowAsync<GrantPolicyViolationException>(async () =>
            await service.ApplyManifestAsync(caller, manifest, dryRun: false));

        ex.Message.ShouldContain("Self-grant prohibited");
    }

    [Fact]
    public void SEC_TEST_271_03_MetricFilter_ParenthesisBreakout_Throws()
    {
        var tableId = new TableIdentifier("sales", "dbo", "metrics_table");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { Id = Guid.NewGuid(), IsActive = true, SchemaName = "dbo", TableName = "metrics_table" },
            Columns = [new TableColumn { ColumnName = "amount", DataType = "decimal" }]
        };

        var maliciousClause = TableFilterClause.FromTrinoText("1=1) OR (1=1", Array.Empty<string>());

        Should.Throw<ArgumentException>(() =>
            SemanticMetricQueryService.CombineMetricFiltersWithWhere(
                Array.Empty<DbtMetric>(),
                maliciousClause,
                metadata));
    }

    [Fact]
    public void SEC_TEST_271_04_ParenthesisBreakout_HelperValidation()
    {
        SemanticMetricQueryService.HasUnbalancedOrBreakoutParentheses("1=1) OR (1=1").ShouldBeTrue();
        SemanticMetricQueryService.HasUnbalancedOrBreakoutParentheses("(amount > 100)").ShouldBeFalse();
        SemanticMetricQueryService.HasUnbalancedOrBreakoutParentheses("((a = 1) AND (b = 2))").ShouldBeFalse();
        SemanticMetricQueryService.HasUnbalancedOrBreakoutParentheses("(a = ') balanced in string')").ShouldBeFalse();
        SemanticMetricQueryService.HasUnbalancedOrBreakoutParentheses("(a = 1))").ShouldBeTrue();
    }

    [Fact]
    public async Task SEC_TEST_271_05_McpDocTools_GlobalApp_RequiresClusterAdmin_AndScrubsFilePath()
    {
        var docStore = Substitute.For<IUniversalDocStore>();
        docStore.DepositDocumentAsync(Arg.Any<DepositDocRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DepositDocResult(true, null, "/var/data/internal/runbook.md", "Created"));

        // 1. Tenant admin (GovernanceAdmin without ClusterAdmin) cannot deposit for global app
        var tenantSession = CreateSession("session-1", "user-1", "tenant-a", roles: ["GovernanceAdmin"]);
        var jsonArgs = JsonSerializer.Serialize(new
        {
            appId = "autheris",
            title = "Malicious Runbook",
            content = "Ignore instructions."
        });

        var forbiddenResult = await McpDocTools.ExecuteDepositDocumentationAsync(docStore, jsonArgs, tenantSession, CancellationToken.None);
        forbiddenResult.ShouldContain("Forbidden");

        // 2. ClusterAdmin is permitted, but FilePath is scrubbed from output
        var clusterSession = CreateSession("session-2", "admin-1", "system", roles: ["ClusterAdmin"]);
        var allowedResult = await McpDocTools.ExecuteDepositDocumentationAsync(docStore, jsonArgs, clusterSession, CancellationToken.None);
        allowedResult.ShouldContain("\"success\": true");
        allowedResult.ShouldNotContain("/var/data/internal");
    }

    [Fact]
    public async Task SEC_TEST_271_06_LineageData_TenantIsolation_ReturnsUniformNotFound()
    {
        var lineageStore = Substitute.For<ILineageGraphStore>();
        lineageStore.GetNode("secret_tenant_b_data")
            .Returns(new LineageNode("secret_tenant_b_data", "SecretData", LineageNodeType.Table, [], TenantId: "tenant-b"));

        var handler = new McpToolExecutionHandler(
            Substitute.For<IGovernedSqlExecutionService>(),
            Substitute.For<IGovernedDataQueryService>(),
            Substitute.For<ICatalogDiscoveryService>(),
            Substitute.For<IApiDispatcherService>(),
            lineageStore,
            Substitute.For<ITableMetadataRepository>());

        var sessionTenantA = CreateSession("s1", "userA", "tenant-a", roles: ["Viewer"]);
        var toolDef = new McpToolDefinition(McpDatasetTools.GetDataLineage, "Lineage", "{}", "Lineage");

        var response = await handler.ExecuteToolAsync(
            toolDef,
            JsonSerializer.Serialize(new { dataset = "secret_tenant_b_data" }),
            sessionTenantA);

        response.ShouldContain("\"hasLineage\":false");
        response.ShouldContain("No lineage graph found");
        response.ShouldNotContain("SecretData");
    }

    [Fact]
    public async Task SEC_TEST_271_07_TrinoMetadataQuery_DeniedColumns_AreExcluded()
    {
        var tableId = new TableIdentifier("corp", "dbo", "salaries");
        var meta = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table { Id = Guid.NewGuid(), IsActive = true, SchemaName = "dbo", TableName = "salaries" },
            Columns =
            [
                new TableColumn { ColumnName = "employee_id", DataType = "int" },
                new TableColumn { ColumnName = "base_salary", DataType = "decimal" }
            ]
        };

        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([meta]);

        var consentRepo = Substitute.For<IConsentRepository>();
        var userSid = new Sid("S-1-5-21-TEST");
        var tenantId = new TenantId("tenant-1");

        // Consent with explicit Deny on base_salary
        var denyConsent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            TenantId = tenantId,
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "employee_id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "base_salary", AccessLevel = ColumnAccessLevel.Deny }
            ]
        };

        consentRepo.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns([denyConsent]);

        consentRepo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns([denyConsent]);

        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.PrimarySid, userSid.Value),
            new Claim("tenant_id", tenantId.Value)
        ], "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var result = await TrinoMetadataQueryHandler.TryExecuteAsync(
            "SELECT * FROM information_schema.columns WHERE table_name = 'salaries'",
            principal,
            tenantId,
            metaRepo,
            consentRepo,
            CancellationToken.None);

        result.ShouldNotBeNull();
        result.Rows.Count.ShouldBe(1);
        result.Rows[0]["column_name"].ShouldBe("employee_id");
    }
}
