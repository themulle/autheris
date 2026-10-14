namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Mcp.Tools;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// SR-H-01: Four-Eyes / ABAC / inactive checks must run on every table a query_sql / invoke_api call really reads.
/// SR-H-02 (+ SR-M-09): the MCP admin tools are cluster-admin only and unavailable to read-only sessions.
/// </summary>
public sealed class SecurityReviewMcpHighTests
{
    private static readonly TableIdentifier Secret = new("sales", "public", "secret");
    private static readonly TableIdentifier Pub = new("sales", "public", "pub");

    private sealed class NoDemo : IDemoDataSwitch
    {
        public bool Enabled => false;
    }

    private static AiDataGuardrailService Guardrail(
        IMcpQueryExecutor executor,
        IAccessPlanningService? planning = null,
        IPolicyEnforcementService? policy = null)
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.Arg<TableIdentifier>();
                if (id.Equals(Secret))
                {
                    return new TableMetadata { Identifier = id, Table = new Table { TableName = "secret", RequiresFourEyes = true, IsActive = true } };
                }

                return id.Equals(Pub)
                    ? new TableMetadata { Identifier = id, Table = new Table { TableName = "pub", IsActive = true } }
                    : null;
            });

        return new AiDataGuardrailService(
            new McpToolRegistry(Options.Create(new GatewayOptions()), new NoDemo()),
            Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } }),
            NullLogger<AiDataGuardrailService>.Instance,
            queryExecutor: executor,
            policyEnforcementService: policy,
            tableMetadataRepository: metadataRepo,
            accessPlanningService: planning);
    }

    private static IMcpQueryExecutor Executor()
    {
        var executor = Substitute.For<IMcpQueryExecutor>();
        executor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("""{"rows":[]}"""));
        return executor;
    }

    private static McpSessionContext Session(IReadOnlyList<string>? roles = null, bool readOnly = false) =>
        new("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER", Roles: roles, IsReadOnly: readOnly);

    private static string SqlArgs(string sql) => JsonSerializer.Serialize(new { query = sql });

    [Theory]
    [InlineData("SELECT p.id FROM sales.public.pub p JOIN sales.public.secret s ON s.id = p.id")]
    [InlineData("SELECT * FROM sales.public.pub, sales.public.secret")]
    [InlineData("SELECT * FROM sales.public.pub WHERE id IN (SELECT id FROM sales.public.secret)")]
    [InlineData("SELECT id FROM sales.public.pub UNION ALL SELECT id FROM sales.public.secret")]
    [InlineData("WITH x AS (SELECT * FROM sales.public.secret) SELECT * FROM x")]
    [InlineData("SELECT * FROM sales.public.secret")]
    [InlineData("SELECT /* FROM sales.public.pub */ * FROM sales.public.secret")]
    [InlineData("SELECT 'FROM sales.public.pub' AS decoy, s.id FROM sales.public.secret s")]
    public async Task SR_H_01_QuerySql_AppliesFourEyes_ToEveryReferencedTable(string sql)
    {
        var result = await Guardrail(Executor()).ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("query_sql", SqlArgs(sql)), Session());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("Four-Eyes");
    }

    [Theory]
    [InlineData("SELECT FROM WHERE ((")]
    [InlineData("not sql at all")]
    public async Task SR_H_01_QuerySql_UnparseableSql_FailsClosed(string sql)
    {
        var executor = Executor();
        var result = await Guardrail(executor).ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("query_sql", SqlArgs(sql)), Session());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("could not be resolved");
        await executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task SR_H_01_QuerySql_OnlyPublicTable_IsAllowed()
    {
        var result = await Guardrail(Executor()).ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("query_sql", SqlArgs("SELECT /* sales.public.secret */ id FROM sales.public.pub")), Session());

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
    }

    [Fact]
    public async Task SR_H_01_QuerySql_AbacRunsOnEveryTable()
    {
        var policy = Substitute.For<IPolicyEnforcementService>();
        policy.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(ci.Arg<SecurityEvaluationContext>().TargetTable.Equals(Secret)
                ? TableAccessDecision.Denied(Secret, "no policy")
                : new TableAccessDecision(Pub, true, new Dictionary<string, ColumnAccessLevel>(), null, [])));
        var options = Options.Create(new GatewayOptions { Casbin = new CasbinOptions { Enabled = true }, Mcp = new McpOptions { Enabled = true } });
        var guardrail = new AiDataGuardrailService(
            new McpToolRegistry(Options.Create(new GatewayOptions()), new NoDemo()), options,
            NullLogger<AiDataGuardrailService>.Instance, queryExecutor: Executor(), policyEnforcementService: policy);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("query_sql", SqlArgs("SELECT * FROM sales.public.pub p JOIN sales.public.secret s ON s.id = p.id")), Session());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("ABAC");
    }

    [Theory]
    [InlineData("""{"endpoint":"/api/v1/data/sales/public/secret","method":"GET"}""")]
    [InlineData("""{"endpoint":"/API/V1/DATA/sales/public/secret","method":"GET"}""")]
    [InlineData("""{"endpoint":"/api/v1/data/sales/public/pub","method":"GET","parameters":{"dataset":"sales.public.secret"}}""")]
    [InlineData("""{"endpoint":"/api/v1/data","method":"GET","parameters":{"dataset":"sales.public.secret"}}""")]
    [InlineData("""{"endpoint":"/api/v1/statement","method":"POST","body":{"sql":"SELECT * FROM sales.public.pub a JOIN sales.public.secret b ON a.id=b.id"}}""")]
    [InlineData("""{"endpoint":"/api/v1/statement","method":"POST","parameters":{"sql":"SELECT * FROM sales.public.secret"}}""")]
    [InlineData("""{"endpoint":"/api/v1/statement","method":"POST","body":{"query":"SELECT * FROM sales.public.secret"}}""")]
    public async Task SR_H_01_InvokeApi_AppliesFourEyes_ToTheRealTargetTable(string args)
    {
        var result = await Guardrail(Executor()).ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("invoke_api", args), Session());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("Four-Eyes");
    }

    [Theory]
    [InlineData("""{"endpoint":"/api/v1/data","method":"GET"}""")]
    [InlineData("""{"endpoint":"/api/v1/data/onlydomain","method":"GET"}""")]
    [InlineData("""{"endpoint":"/api/v1/statement","method":"POST","body":{"sql":"SELECT ((("}}""")]
    [InlineData("""{"endpoint":"/api/v1/statement","method":"POST"}""")]
    public async Task SR_H_01_InvokeApi_UnresolvableDataTarget_FailsClosed(string args)
    {
        var executor = Executor();
        var result = await Guardrail(executor).ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("invoke_api", args), Session());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("could not be resolved");
        await executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
    }

    [Theory]
    [InlineData("""{"endpoint":"/api/v1/catalog/datasets","method":"GET"}""")]
    [InlineData("""{"endpoint":"/api/v1/data/sales/public/pub","method":"GET"}""")]
    [InlineData("""{"endpoint":"/api/v1/statement","method":"POST","body":{"sql":"SELECT id FROM sales.public.pub"}}""")]
    public async Task SR_H_01_InvokeApi_NonFourEyesTargets_AreAllowed(string args)
    {
        var result = await Guardrail(Executor()).ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("invoke_api", args), Session());

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
    }

    private static readonly string[] AdminToolNames =
    [
        McpAdminTools.PlanAccess, McpAdminTools.ApplyAccess, McpAdminTools.RegisterDatasource,
        McpAdminTools.SetDatasetState, McpAdminTools.ResolvePrincipal
    ];

    public static TheoryData<string, string> AdminToolsAndRoles()
    {
        var data = new TheoryData<string, string>();
        foreach (var tool in AdminToolNames)
        {
            foreach (var role in new[] { "TenantAdmin", "Admin", "GovernanceAdmin", "GatewayAdmin", "tenant-a:ClusterAdmin" })
            {
                data.Add(tool, role);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AdminToolsAndRoles))]
    public async Task SR_H_02_NonClusterAdminRoles_AreDeniedAdminTools(string tool, string role)
    {
        var planning = Substitute.For<IAccessPlanningService>();
        var result = await Guardrail(Executor(), planning).ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(tool, "{}"), Session([role]));

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("Access denied");
        planning.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("admin_resolve_principal")]
    [InlineData("admin_plan_access")]
    public async Task SR_H_02_ClusterAdmin_IsAllowedAdminTools(string tool)
    {
        var planning = Substitute.For<IAccessPlanningService>();
        var result = await Guardrail(Executor(), planning).ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(tool, "{}"), Session(["ClusterAdmin"]));

        // Authorization passed: any failure comes from the tool itself (e.g. empty arguments), not from the gate.
        (result.ErrorMessage ?? string.Empty).ShouldNotContain("Access denied");
    }

    [Theory]
    [InlineData("admin_plan_access")]
    [InlineData("admin_apply_access")]
    [InlineData("admin_register_datasource")]
    [InlineData("admin_set_dataset_state")]
    [InlineData("admin_resolve_principal")]
    public async Task SR_H_02_ReadOnlySession_IsDeniedAdminTools_EvenForClusterAdmin(string tool)
    {
        var planning = Substitute.For<IAccessPlanningService>();
        var result = await Guardrail(Executor(), planning).ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(tool, "{}"), Session(["ClusterAdmin"], readOnly: true));

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("Access denied");
        planning.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("TenantAdmin", false, false)]
    [InlineData("Admin", false, false)]
    [InlineData("GovernanceAdmin", false, false)]
    [InlineData("ClusterAdmin", false, true)]
    [InlineData("ClusterAdmin", true, false)]
    public void SR_H_02_AdminToolVisibility_IsClusterAdminOnly(string role, bool readOnly, bool visible)
    {
        McpAdminTools.CanUseAdminTools(Session([role], readOnly), authBypassed: false).ShouldBe(visible);
    }

    [Theory]
    [InlineData("TenantAdmin", false)]
    [InlineData("Admin", false)]
    [InlineData("ClusterAdmin", true)]
    public async Task SR_H_02_ToolsList_HidesAdminToolsFromNonClusterAdmins(string role, bool visible)
    {
        var registry = new McpToolRegistry(Options.Create(new GatewayOptions()), new NoDemo());
        var store = Substitute.For<IMcpSessionStore>();
        var session = Session([role]);
        store.GetSessionAsync("s1", Arg.Any<CancellationToken>()).Returns(new ValueTask<McpSessionContext?>(session));
        var handler = new McpProtocolHandler(store, registry, Substitute.For<IAiDataGuardrailService>(),
            NullLogger<McpProtocolHandler>.Instance, options: Options.Create(new GatewayOptions()));

        var json = await handler.HandleMessageAsync("s1", """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");

        json.ShouldNotBeNull();
        json.Contains("admin_set_dataset_state", StringComparison.Ordinal).ShouldBe(visible);
    }
}
