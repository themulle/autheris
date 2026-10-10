namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Mcp.Tools;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class AdminMcpTwoPhaseTests
{
    private const string AdminSid = "S-1-5-21-99999999-500";
    private const string DatasetId = "sales.public.orders";

    private readonly ITableMetadataRepository _tableRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IRebacStore _rebacStore = Substitute.For<IRebacStore>();
    private readonly IAuditLogRepository _auditRepo = Substitute.For<IAuditLogRepository>();
    private readonly ITotpSecretStore _totpSecretStore = new InMemoryTotpSecretStore();
    private readonly ITotpVerificationService _totpService;
    private readonly TableMetadata _mockMetadata;
    private readonly IAccessPlanningService _service;

    public AdminMcpTwoPhaseTests()
    {
        _totpService = new TotpVerificationService();
        _service = new AccessPlanningService(_auditRepo, _tableRepo, _rebacStore, _totpService, _totpSecretStore);

        var tableId = Guid.NewGuid();
        _mockMetadata = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "public", "orders"),
            Table = new Table
            {
                Id = tableId,
                SchemaName = "public",
                TableName = "orders",
                Sensitivity = "CONFIDENTIAL",
                IsActive = true
            },
            Columns = new List<TableColumn>
            {
                new() { TableId = tableId, ColumnName = "id", DataType = "int", IsSensitive = false },
                new() { TableId = tableId, ColumnName = "total_amount", DataType = "numeric", IsSensitive = false },
                new() { TableId = tableId, ColumnName = "customer_name", DataType = "varchar", IsSensitive = true },
                new() { TableId = tableId, ColumnName = "customer_email", DataType = "varchar", IsSensitive = true }
            },
            PrimaryKeyColumns = ["id"]
        };

        _tableRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(_mockMetadata);
    }

    [Fact]
    public async Task AdminPlanAccess_GeneratesDiffAndWarnings_WithoutModifyingState()
    {
        // Arrange
        var service = _service;
        var request = new AdminPlanAccessRequest(
            DatasetId: DatasetId,
            Grants: new List<PrincipalAccessGrant>
            {
                new(
                    Principal: "analyst_bob",
                    Columns: new Dictionary<string, string>
                    {
                        ["id"] = "clear",
                        ["customer_name"] = "clear",   // Sensitive PII in clear text -> Warning!
                        ["customer_email"] = "mask",   // Sensitive PII masked
                        ["total_amount"] = "clear"
                    },
                    RowFilter: "country = 'DE'")
            },
            Reason: "Q4 financial audit");

        // Act
        var result = await service.PlanAccessAsync(request, AdminSid);

        // Assert
        result.ShouldNotBeNull();
        result.PlanId.ShouldNotBeNullOrWhiteSpace();
        result.DatasetId.ShouldBe(DatasetId);
        result.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);

        // Diffs verification
        result.Diffs.Count.ShouldBe(4);
        var nameDiff = result.Diffs.FirstOrDefault(d => d.Column == "customer_name");
        nameDiff.ShouldNotBeNull();
        nameDiff.IsPii.ShouldBeTrue();
        nameDiff.AfterState.ShouldBe("clear");

        var emailDiff = result.Diffs.FirstOrDefault(d => d.Column == "customer_email");
        emailDiff.ShouldNotBeNull();
        emailDiff.IsPii.ShouldBeTrue();
        emailDiff.AfterState.ShouldBe("mask");

        // Security Warning verification (PII in cleartext)
        result.Warnings.ShouldNotBeEmpty();
        result.Warnings.Any(w => w.Contains("customer_name") && w.Contains("PII", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();

        // Invariant: Zero mutations to ReBAC store and Table metadata during planning phase!
        await _rebacStore.DidNotReceiveWithAnyArgs().AddTupleAsync(default!);
        await _rebacStore.DidNotReceiveWithAnyArgs().AddTuplesAsync(default!);
        await _tableRepo.DidNotReceiveWithAnyArgs().UpsertTableMetadataAsync(default!);
    }

    [Fact]
    public async Task AdminApplyAccess_WithoutConfirmationToken_FailsWithForbidden()
    {
        // Arrange
        var service = _service;
        var applyWithoutToken = new AdminApplyAccessRequest("plan-12345", string.Empty);

        // Act & Assert
        await Should.ThrowAsync<SecurityException>(async () =>
        {
            await service.ApplyAccessAsync(applyWithoutToken, AdminSid);
        });

        await _rebacStore.DidNotReceiveWithAnyArgs().AddTuplesAsync(default!);
        await _auditRepo.DidNotReceiveWithAnyArgs().RecordAuditEventAsync(default!);
    }

    [Fact]
    public async Task ConfirmPlan_WithValidTotp_IssuesConfirmationToken()
    {
        // Arrange
        var service = _service;
        var planRequest = new AdminPlanAccessRequest(
            DatasetId: DatasetId,
            Grants: [new("user_carol", new Dictionary<string, string> { ["id"] = "clear" })],
            Reason: "Maintenance");

        var planResult = await service.PlanAccessAsync(planRequest, AdminSid);
        var enrollment = _totpService.GenerateEnrollment(AdminSid, "admin@autheris.local");
        await _totpSecretStore.SetSecretAsync(AdminSid, enrollment.SecretBase32);

        var validTotp = _totpService.GenerateTotpCode(enrollment.SecretBase32);

        // Act
        var confirmResult = await service.ConfirmPlanAsync(planResult.PlanId, validTotp, AdminSid);

        // Assert
        confirmResult.ShouldNotBeNull();
        confirmResult.ConfirmationToken.ShouldNotBeNullOrWhiteSpace();
        confirmResult.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        (confirmResult.ExpiresAt - DateTimeOffset.UtcNow).TotalMinutes.ShouldBeLessThanOrEqualTo(11);
    }

    [Fact]
    public async Task AdminApplyAccess_WithValidConfirmationToken_AppliesMutationsAndCreatesWormAudit()
    {
        // Arrange
        var service = _service;
        var planRequest = new AdminPlanAccessRequest(
            DatasetId: DatasetId,
            Grants: [new("analyst_bob", new Dictionary<string, string> { ["id"] = "clear", ["total_amount"] = "clear" })],
            Reason: "Authorized financial report");

        var planResult = await service.PlanAccessAsync(planRequest, AdminSid);
        var enrollment = _totpService.GenerateEnrollment(AdminSid, "admin@autheris.local");
        await _totpSecretStore.SetSecretAsync(AdminSid, enrollment.SecretBase32);
        var validTotp = _totpService.GenerateTotpCode(enrollment.SecretBase32);

        var confirmResult = await service.ConfirmPlanAsync(planResult.PlanId, validTotp, AdminSid);

        // Act
        var applyResult = await service.ApplyAccessAsync(
            new AdminApplyAccessRequest(planResult.PlanId, confirmResult.ConfirmationToken),
            AdminSid);

        // Assert
        applyResult.Success.ShouldBeTrue();
        applyResult.AppliedTuplesCount.ShouldBeGreaterThan(0);

        // ReBAC mutation verification
        await _rebacStore.Received(1).AddTuplesAsync(
            Arg.Is<IEnumerable<RebacTuple>>(tuples => tuples.Any(t => t.User.Contains("analyst_bob"))),
            Arg.Any<CancellationToken>());

        // WORM Audit Entry verification
        await _auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(entry =>
                entry.EventType == "ADMIN_APPLY_ACCESS" &&
                entry.TargetTable == DatasetId &&
                entry.ActorSid.Value == AdminSid &&
                entry.DetailsJson.Contains(planResult.PlanId)),
            Arg.Any<CancellationToken>());

        // Single-use guarantee: Replaying the same confirmation token must fail!
        await Should.ThrowAsync<SecurityException>(async () =>
        {
            await service.ApplyAccessAsync(
                new AdminApplyAccessRequest(planResult.PlanId, confirmResult.ConfirmationToken),
                AdminSid);
        });
    }

    [Fact]
    public async Task AdminRegisterDatasource_InactiveStatusAndSecretsStoredInVault_NoLeakageInAudit()
    {
        // Arrange
        var service = _service;
        const string cleartextSecret = "super-secret-vault-api-key-99999";
        var request = new AdminRegisterDatasourceRequest(
            Name: "sap_erp",
            Domain: "finance",
            BaseUrl: "https://erp.internal.corp",
            Auth: new DatasourceAuthDto(
                Type: "bearer",
                Secret: cleartextSecret),
            DryRun: false);

        // Act
        var result = await service.RegisterDatasourceAsync(request, AdminSid);

        // Assert
        result.ShouldNotBeNull();
        result.Status.ShouldBe("inactive"); // SEC M-30 Inactive on creation
        result.IsConfigured.ShouldBeTrue();
        result.SecretRef.ShouldNotBeNullOrWhiteSpace();
        result.SecretRef.ShouldStartWith("vault://");

        // Cleartext secret MUST NEVER be returned in result
        result.ToString().ShouldNotContain(cleartextSecret);

        // WORM Audit log check: secret must never be written to audit logs
        await _auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(entry =>
                entry.EventType == "ADMIN_REGISTER_DATASOURCE" &&
                entry.ActorSid.Value == AdminSid &&
                !entry.DetailsJson.Contains(cleartextSecret) &&
                entry.DetailsJson.Contains("isConfigured")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdminSetDatasetState_ChangesStatusWithAudit()
    {
        // Arrange
        var service = _service;
        var request = new AdminSetDatasetStateRequest(
            DatasetId: DatasetId,
            State: "quarantined",
            Reason: "Anomalous data drift detected by dbt test");

        // Act
        var result = await service.SetDatasetStateAsync(request, AdminSid);

        // Assert
        result.Success.ShouldBeTrue();
        result.DatasetId.ShouldBe(DatasetId);
        result.PreviousState.ShouldBe("active");
        result.NewState.ShouldBe("quarantined");

        // Metadata update verification
        await _tableRepo.Received(1).UpsertTableMetadataAsync(
            Arg.Is<TableMetadata>(m => m.Table.IsActive == false),
            Arg.Any<CancellationToken>());

        // WORM Audit entry verification
        await _auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(entry =>
                entry.EventType == "ADMIN_SET_DATASET_STATE" &&
                entry.TargetTable == DatasetId &&
                entry.DetailsJson.Contains("quarantined") &&
                entry.DetailsJson.Contains("data drift")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdminResolvePrincipal_FuzzySearch_ReturnsMatchingPrincipals()
    {
        // Arrange
        var service = _service;

        // Act: search with fuzzy query "philipp"
        var result = await service.ResolvePrincipalAsync(new AdminResolvePrincipalRequest("philipp"), AdminSid);

        // Assert
        result.ShouldNotBeNull();
        result.Matches.ShouldNotBeEmpty();
        var match = result.Matches.First();
        match.DisplayName.ShouldContain("Philipp", Case.Insensitive);
        match.Sid.ShouldNotBeNullOrWhiteSpace();
        match.PrincipalType.ShouldNotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("admin_plan_access")]
    [InlineData("admin_apply_access")]
    [InlineData("admin_register_datasource")]
    [InlineData("admin_set_dataset_state")]
    [InlineData("admin_resolve_principal")]
    public void AdminMcpTools_AreRegistered_InMcpToolRegistry(string toolName)
    {
        var registry = new McpToolRegistry(Options.Create(new GatewayOptions()));
        var tool = registry.FindTool(toolName);

        tool.ShouldNotBeNull();
        tool.Name.ShouldBe(toolName);
        tool.Description.ShouldNotBeNullOrWhiteSpace();
        using var doc = JsonDocument.Parse(tool.InputJsonSchema);
        doc.RootElement.GetProperty("type").GetString().ShouldBe("object");
    }

    [Fact]
    public async Task AdminMcp_ExecutionViaGuardrail_BlocksNonAdmin()
    {
        // Arrange
        var registry = new McpToolRegistry(Options.Create(new GatewayOptions()));
        var guardrail = new AiDataGuardrailService(
            registry,
            Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } }),
            NullLogger<AiDataGuardrailService>.Instance,
            accessPlanningService: _service);

        var nonAdminSession = new McpSessionContext(
            SessionId: "sess-user-1",
            ServicePrincipalId: "analyst-bob",
            TenantId: "default",
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow,
            Roles: ["DataAnalyst"]); // No admin role!

        var request = new McpToolCallRequest(
            ToolName: McpAdminTools.PlanAccess,
            ArgumentsJson: JsonSerializer.Serialize(new
            {
                datasetId = DatasetId,
                grants = new object[]
                {
                    new { principal = "bob", columns = new Dictionary<string, string> { ["id"] = "clear" } }
                },
                reason = "Auditing"
            }));

        // Act
        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, nonAdminSession);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("administrative role");
    }

    [Fact]
    public async Task AdminMcp_ExecutionViaGuardrail_AllowsAdmin_AndExecutesPlan()
    {
        // Arrange
        var registry = new McpToolRegistry(Options.Create(new GatewayOptions()));
        var guardrail = new AiDataGuardrailService(
            registry,
            Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } }),
            NullLogger<AiDataGuardrailService>.Instance,
            accessPlanningService: _service);

        var adminSession = new McpSessionContext(
            SessionId: "sess-admin-1",
            ServicePrincipalId: AdminSid,
            TenantId: "default",
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow,
            Roles: ["GovernanceAdmin"]);

        var planCall = new McpToolCallRequest(
            ToolName: McpAdminTools.PlanAccess,
            ArgumentsJson: JsonSerializer.Serialize(new
            {
                datasetId = DatasetId,
                grants = new object[]
                {
                    new { principal = "analyst_bob", columns = new Dictionary<string, string> { ["customer_name"] = "clear" } }
                },
                reason = "Quarterly compliance review"
            }));

        // Act
        var result = await guardrail.ExecuteToolWithGuardrailAsync(planCall, adminSession);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.ContentJson.ShouldContain("plan_");
        result.ContentJson.ShouldContain("customer_name");
        result.ContentJson.ShouldContain("PII");
    }

    [Fact]
    public async Task AdminConfirmAndApplyAccess_WhenAdminSidContainsColons_Succeeds()
    {
        // Arrange: adminSid has colons (e.g. user:david.admin or tenant:cluster:admin)
        const string colonAdminSid = "user:david:governance_admin";
        var planRequest = new AdminPlanAccessRequest(
            DatasetId: DatasetId,
            Grants: [new("user_eve", new Dictionary<string, string> { ["id"] = "clear" })],
            Reason: "ReBAC colon SID test");

        var planResult = await _service.PlanAccessAsync(planRequest, colonAdminSid);
        var enrollment = _totpService.GenerateEnrollment(colonAdminSid, "admin@autheris.local");
        await _totpSecretStore.SetSecretAsync(colonAdminSid, enrollment.SecretBase32);

        var validTotp = _totpService.GenerateTotpCode(enrollment.SecretBase32);

        // Act 1: Confirm with colon adminSid
        var confirmResult = await _service.ConfirmPlanAsync(planResult.PlanId, validTotp, colonAdminSid);
        confirmResult.ShouldNotBeNull();
        confirmResult.ConfirmationToken.ShouldNotBeNullOrWhiteSpace();

        // Act 2: Apply with the confirmation token
        var applyResult = await _service.ApplyAccessAsync(
            new AdminApplyAccessRequest(planResult.PlanId, confirmResult.ConfirmationToken),
            colonAdminSid);

        // Assert
        applyResult.Success.ShouldBeTrue();
        applyResult.AppliedTuplesCount.ShouldBeGreaterThan(0);
    }
}
