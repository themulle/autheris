namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
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
using Autheris.Application.Security.Totp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>Regression tests for SR-H-01 (MCP Four-Eyes / ABAC table resolution) and SR-H-02 (MCP admin tool gate).</summary>
public sealed class McpSecurityReviewWaveATests
{
    private static readonly TableIdentifier SalariesTable = new("finance", "dbo", "salaries");

    private static McpSessionContext Session(params string[] roles) =>
        new("sess-wave-a", "agent-wave-a", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Roles: roles);

    private static (AiDataGuardrailService Guardrail, IMcpQueryExecutor Executor, IPolicyEnforcementService Policy) BuildGuardrail(bool withSqlEngine = true)
    {
        var options = Options.Create(new GatewayOptions
        {
            Mcp = new McpOptions { Enabled = true },
            Casbin = new CasbinOptions { Enabled = true }
        });
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(call => new TableMetadata
            {
                Table = new Table
                {
                    RequiresFourEyes = call.Arg<TableIdentifier>().Equals(SalariesTable),
                    IsActive = true
                }
            });
        var executor = Substitute.For<IMcpQueryExecutor>();
        var policy = Substitute.For<IPolicyEnforcementService>();
        policy.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(call => new TableAccessDecision(
                call.Arg<SecurityEvaluationContext>().TargetTable, true, new Dictionary<string, ColumnAccessLevel>(), null, []));

        var guardrail = new AiDataGuardrailService(
            new McpToolRegistry(),
            options,
            NullLogger<AiDataGuardrailService>.Instance,
            auditLogRepository: Substitute.For<IAuditLogRepository>(),
            queryExecutor: executor,
            policyEnforcementService: policy,
            tableMetadataRepository: metadataRepo,
            sqlEngine: withSqlEngine ? new FastSqlEngine() : null);
        return (guardrail, executor, policy);
    }

    private static string QueryArgs(string sql) => JsonSerializer.Serialize(new { query = sql });

    private static async Task AssertNotExecuted(IMcpQueryExecutor executor)
    {
        await executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
    }

    [Theory]
    [InlineData("SELECT s.* FROM public.dbo.dummy d JOIN finance.dbo.salaries s ON 1=1")]
    [InlineData("SELECT /* FROM public.dbo.dummy */ * FROM finance.dbo.salaries")]
    [InlineData("SELECT * FROM public.dbo.dummy WHERE id IN (SELECT id FROM finance.dbo.salaries)")]
    [InlineData("SELECT * FROM public.dbo.dummy UNION ALL SELECT * FROM finance.dbo.salaries")]
    [InlineData("SELECT * FROM public.dbo.dummy, finance.dbo.salaries")]
    public async Task SR_H_01_QuerySql_FourEyesTableBehindJoinCommentOrSubquery_IsStillGated(string sql)
    {
        var (guardrail, executor, _) = BuildGuardrail();

        var result = await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest("query_sql", QueryArgs(sql)), Session());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("Four-Eyes");
        await AssertNotExecuted(executor);
    }

    [Fact]
    public async Task SR_H_01_QuerySql_AbacPreCheckRunsForEveryReferencedTable()
    {
        var (guardrail, _, policy) = BuildGuardrail();

        await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("query_sql", QueryArgs("SELECT s.* FROM public.dbo.dummy d JOIN finance.dbo.salaries s ON 1=1")), Session());

        await policy.Received().EvaluatePolicyAsync(
            Arg.Is<SecurityEvaluationContext>(c => c.TargetTable.Equals(SalariesTable)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("this is not sql at all ((")]
    [InlineData("SELECT 1")]
    public async Task SR_H_01_QuerySql_UnresolvableTables_FailClosed(string sql)
    {
        var (guardrail, executor, _) = BuildGuardrail();

        var result = await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest("query_sql", QueryArgs(sql)), Session());

        result.IsSuccess.ShouldBeFalse();
        await AssertNotExecuted(executor);
    }

    [Fact]
    public async Task SR_H_01_QuerySql_WithoutSqlEngine_FailClosed()
    {
        var (guardrail, executor, _) = BuildGuardrail(withSqlEngine: false);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("query_sql", QueryArgs("SELECT * FROM finance.dbo.salaries")), Session());

        result.IsSuccess.ShouldBeFalse();
        await AssertNotExecuted(executor);
    }

    [Fact]
    public async Task SR_H_01_InvokeApi_DataEndpoint_ResolvesRealTableForFourEyes()
    {
        var (guardrail, executor, _) = BuildGuardrail();

        var result = await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("invoke_api", """{"endpoint":"/api/v1/data/finance/dbo/salaries","method":"GET"}"""), Session());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("Four-Eyes");
        await AssertNotExecuted(executor);
    }

    [Fact]
    public async Task SR_H_01_InvokeApi_StatementEndpoint_ResolvesTablesFromBodySql()
    {
        var (guardrail, executor, _) = BuildGuardrail();

        var result = await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("invoke_api", """{"endpoint":"/api/v1/statement","method":"POST","body":{"sql":"SELECT * FROM public.dbo.dummy d JOIN finance.dbo.salaries s ON 1=1"}}"""), Session());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("Four-Eyes");
        await AssertNotExecuted(executor);
    }

    [Theory]
    [InlineData("""{"endpoint":"/api/v1/data","method":"GET"}""")]
    [InlineData("""{"endpoint":"/api/v1/statement","method":"POST","body":{"sql":"nonsense (("}}""")]
    [InlineData("""{"endpoint":"/api/v1/statement","method":"POST"}""")]
    public async Task SR_H_01_InvokeApi_UnresolvableDataTarget_FailClosed(string args)
    {
        var (guardrail, executor, _) = BuildGuardrail();

        var result = await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest("invoke_api", args), Session());

        result.IsSuccess.ShouldBeFalse();
        await AssertNotExecuted(executor);
    }

    // ---- SR-H-02 ----

    [Theory]
    [InlineData("TenantAdmin", false)]
    [InlineData("Admin", false)]
    [InlineData("tenant-a:TenantAdmin", false)]
    [InlineData("Consumer", false)]
    [InlineData("ClusterAdmin", true)]
    [InlineData("GovernanceAdmin", true)]
    public void SR_H_02_AdminToolGate_AdmitsOnlyClusterWideRoles(string role, bool expected)
    {
        McpAdminTools.IsAdminCaller([role], authBypassed: false).ShouldBe(expected);
    }

    [Fact]
    public void SR_H_02_AdminToolGate_NullRoles_Denied_BypassAdmits()
    {
        McpAdminTools.IsAdminCaller(null, authBypassed: false).ShouldBeFalse();
        McpAdminTools.IsAdminCaller(null, authBypassed: true).ShouldBeTrue();
    }

    [Fact]
    public async Task SR_H_02_Guardrail_TenantAdmin_CannotExecuteAdminTool()
    {
        var planning = Substitute.For<IAccessPlanningService>();
        var guardrail = new AiDataGuardrailService(
            new McpToolRegistry(),
            Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } }),
            NullLogger<AiDataGuardrailService>.Instance,
            auditLogRepository: Substitute.For<IAuditLogRepository>(),
            accessPlanningService: planning);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("admin_set_dataset_state", """{"datasetId":"sales.public.orders","state":"inactive"}"""),
            Session("TenantAdmin"));

        result.IsSuccess.ShouldBeFalse();
        await planning.DidNotReceiveWithAnyArgs().SetDatasetStateAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task SR_H_02_McpAdminTools_PassCallerTenantToPlanningService()
    {
        var planning = Substitute.For<IAccessPlanningService>();
        planning.SetDatasetStateAsync(Arg.Any<AdminSetDatasetStateRequest>(), Arg.Any<string>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(new AdminSetDatasetStateResult("sales.public.orders", "active", "inactive", true, "ok"));

        await McpAdminTools.ExecuteAdminToolAsync(
            McpAdminTools.SetDatasetState,
            """{"datasetId":"sales.public.orders","state":"inactive"}""",
            Session("ClusterAdmin"),
            planning);

        await planning.Received(1).SetDatasetStateAsync(
            Arg.Any<AdminSetDatasetStateRequest>(), Arg.Any<string>(),
            Arg.Is<TenantId?>(t => t.HasValue && t.Value.Value == "tenant-a"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SR_H_02_SetDatasetState_AuditsUnderCallerTenant()
    {
        var audit = Substitute.For<IAuditLogRepository>();
        var service = new AccessPlanningService(
            audit, BuildTableRepo(), Substitute.For<IRebacStore>(), new TotpVerificationService(), new InMemoryTotpSecretStore());

        await service.SetDatasetStateAsync(
            new AdminSetDatasetStateRequest("sales.public.orders", "inactive", "test"), "S-1-5-21-1", new TenantId("tenant-a"));

        await audit.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == "ADMIN_SET_DATASET_STATE" && e.TenantId.Value == "tenant-a"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SR_H_02_ApplyAccess_UsesCallerTenantNotDatasetDomainForReBac()
    {
        var rebac = Substitute.For<IRebacStore>();
        var totpStore = new InMemoryTotpSecretStore();
        var totp = new TotpVerificationService();
        var planning = new AccessPlanningService(Substitute.For<IAuditLogRepository>(), BuildTableRepo(), rebac, totp, totpStore);
        const string admin = "S-1-5-21-99999999-500";
        var plan = await planning.PlanAccessAsync(
            new AdminPlanAccessRequest("victim.public.orders", [new("analyst", new Dictionary<string, string> { ["id"] = "clear" })], "r"), admin);
        var enrollment = totp.GenerateEnrollment(admin, "a@b.c");
        await totpStore.SetSecretAsync(admin, enrollment.SecretBase32);
        var confirm = await planning.ConfirmPlanAsync(plan.PlanId, totp.GenerateTotpCode(enrollment.SecretBase32), admin);

        await planning.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, confirm.ConfirmationToken), admin, new TenantId("tenant-a"));

        await rebac.Received(1).AddTuplesAsync(
            Arg.Is<IEnumerable<RebacTuple>>(ts => ts.All(t => t.TenantId == "tenant-a")),
            Arg.Any<CancellationToken>());
    }

    private static ITableMetadataRepository BuildTableRepo()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        var id = Guid.NewGuid();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(call => new TableMetadata
        {
            Identifier = call.Arg<TableIdentifier>(),
            Table = new Table { Id = id, SchemaName = "public", TableName = "orders", Sensitivity = "CONFIDENTIAL", IsActive = true },
            Columns = [new TableColumn { TableId = id, ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"]
        });
        return repo;
    }
}
