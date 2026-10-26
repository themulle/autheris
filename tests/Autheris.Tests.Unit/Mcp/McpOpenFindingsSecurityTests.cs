namespace Autheris.Tests.Unit.Mcp;

using Autheris.Domain.Security;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Mcp;
using Autheris.Api.Middleware;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Mcp.Tools;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Open findings of the MCP security review: get_table_schema batch targets, guarded resource reads, app-only group SIDs,
/// confirmed and audited admin mutations, structured tool errors and the catalog search limit.
/// </summary>
[Trait("Category", "Security")]
public sealed class McpOpenFindingsSecurityTests
{
    private const string AdminSid = "S-1-5-21-99999999-500";
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");
    private static readonly TableIdentifier Payroll = new("hr", "public", "payroll");

    private sealed class NoDemo : IDemoDataSwitch
    {
        public bool Enabled => false;
    }

    private static McpSessionContext UserSession(IReadOnlyList<string>? groups = null, IReadOnlyList<string>? roles = null) =>
        new("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER", Roles: roles, GroupSids: groups);

    private static McpSessionContext AppOnlySession(IReadOnlyList<string>? groups = null) =>
        new("s1", "app-client", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: null, GroupSids: groups);

    private static IPolicyEnforcementService PolicyDenying(params TableIdentifier[] denied)
    {
        var policy = Substitute.For<IPolicyEnforcementService>();
        policy.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var target = ci.Arg<SecurityEvaluationContext>().TargetTable;
                return ValueTask.FromResult(denied.Contains(target)
                    ? TableAccessDecision.Denied(target, "no policy")
                    : TableAccessDecision.Allowed(target, new Dictionary<string, ColumnAccessLevel>()));
            });
        return policy;
    }

    private static IMcpQueryExecutor Executor(string json = """{"totalTables":0,"tables":[]}""")
    {
        var executor = Substitute.For<IMcpQueryExecutor>();
        executor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(json));
        return executor;
    }

    private static AiDataGuardrailService Guardrail(
        IPolicyEnforcementService? policy = null,
        IMcpQueryExecutor? executor = null,
        IAuditLogRepository? audit = null,
        IAccessPlanningService? planning = null) =>
        new(
            new McpToolRegistry(Options.Create(new GatewayOptions()), new NoDemo()),
            Options.Create(new GatewayOptions { Casbin = new CasbinOptions { Enabled = policy != null }, Mcp = new McpOptions { Enabled = true } }),
            NullLogger<AiDataGuardrailService>.Instance,
            audit ?? global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            queryExecutor: executor ?? Executor(),
            policyEnforcementService: policy,
            accessPlanningService: planning);

    private static McpToolDefinition Tool(string name) =>
        new McpToolRegistry(Options.Create(new GatewayOptions()), new NoDemo()).FindTool(name)
        ?? throw new InvalidOperationException($"Tool '{name}' is not registered.");

    // ---- 1. get_table_schema: every requested dataset is an ABAC target -------------------------------------------------------

    [Fact]
    public void GetTableSchema_ResolvesTheSingleDatasetAndEveryBatchEntry()
    {
        var tables = AiDataGuardrailService.ParseTablesFromTool(
            Tool(McpDatasetTools.GetTableSchema), """{"dataset":"sales.public.orders","datasets":["hr.public.payroll","SALES.public.orders"]}""");

        tables.ShouldNotBeNull();
        tables.Count.ShouldBe(2);
        tables.ShouldContain(Orders);
        tables.ShouldContain(Payroll);
    }

    [Fact]
    public void GetTableSchema_WithoutADataset_IsCheckedAsACatalogCall()
    {
        AiDataGuardrailService.ParseTablesFromTool(Tool(McpDatasetTools.GetTableSchema), "{}")!
            .ShouldHaveSingleItem().ShouldBe(McpDatasetTools.CatalogTable);
    }

    [Theory]
    [InlineData("""{"datasets":["sales.public.orders","payroll"]}""")]
    [InlineData("""{"datasets":["sales.public.orders",42]}""")]
    [InlineData("""{"datasets":"hr.public.payroll"}""")]
    [InlineData("""{"dataset":7}""")]
    [InlineData("""{"dataset":"sales.public.orders","Dataset":"hr.public.payroll"}""")]
    [InlineData("""{"datasets":["sales.public.orders"],"DATASETS":["hr.public.payroll"]}""")]
    [InlineData("not json")]
    public void GetTableSchema_MalformedOrUnresolvableTargets_FailClosed(string args)
    {
        AiDataGuardrailService.ParseTablesFromTool(Tool(McpDatasetTools.GetTableSchema), args).ShouldBeNull();
        McpDatasetTools.TryGetTableSchemaDatasets(args, out _).ShouldBeFalse();
    }

    [Fact]
    public void GetTableSchema_TooManyDatasets_FailClosed()
    {
        var names = Enumerable.Range(0, McpDatasetTools.MaxTableSchemaDatasets + 1).Select(i => $"sales.public.t{i}");
        var args = JsonSerializer.Serialize(new { datasets = names });

        McpDatasetTools.TableSchemaTargets(args).ShouldBeNull();
    }

    [Fact]
    public async Task GetTableSchema_ADeniedBatchEntry_DeniesTheWholeCall_AndNothingIsRead()
    {
        var policy = PolicyDenying(Payroll);
        var executor = Executor();

        var result = await Guardrail(policy, executor).ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpDatasetTools.GetTableSchema, """{"dataset":"sales.public.orders","datasets":["hr.public.payroll"]}"""),
            UserSession());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("ABAC");
        await executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task GetTableSchema_UnresolvableBatchEntry_IsDenied_BeforeAnyPolicyOrRead()
    {
        var policy = PolicyDenying();
        var executor = Executor();

        var result = await Guardrail(policy, executor).ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpDatasetTools.GetTableSchema, """{"dataset":"sales.public.orders","datasets":["payroll"]}"""),
            UserSession());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("fail-closed");
        await executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task GetTableSchema_AllowedBatch_ChecksEveryTable()
    {
        var policy = PolicyDenying();

        var result = await Guardrail(policy).ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpDatasetTools.GetTableSchema, """{"dataset":"sales.public.orders","datasets":["hr.public.payroll"]}"""),
            UserSession());

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
        await policy.Received(1).EvaluatePolicyAsync(Arg.Is<SecurityEvaluationContext>(c => c.TargetTable == Orders), Arg.Any<CancellationToken>());
        await policy.Received(1).EvaluatePolicyAsync(Arg.Is<SecurityEvaluationContext>(c => c.TargetTable == Payroll), Arg.Any<CancellationToken>());
    }

    private static (McpToolExecutionHandler Handler, ICatalogDiscoveryService Discovery) Handler()
    {
        var discovery = Substitute.For<ICatalogDiscoveryService>();
        discovery.SearchCatalogDetailedAsync(Arg.Any<CatalogSearchQuery>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CatalogSearchHit>>([]));
        var handler = new McpToolExecutionHandler(
            Substitute.For<IGovernedSqlExecutionService>(),
            Substitute.For<IGovernedDataQueryService>(),
            discovery,
            Substitute.For<IApiDispatcherService>(),
            Substitute.For<ILineageGraphStore>(),
            Substitute.For<ITableMetadataRepository>());
        return (handler, discovery);
    }

    [Fact]
    public async Task Executor_GetTableSchema_ReadsExactlyTheParsedTables()
    {
        var (handler, discovery) = Handler();

        await handler.ExecuteToolAsync(Tool(McpDatasetTools.GetTableSchema),
            """{"dataset":"sales.public.orders","datasets":["hr.public.payroll","sales.public.orders"]}""", UserSession());

        await discovery.Received(1).GetDatasetDetailAsync(Orders, Arg.Any<RequestContext>(), Arg.Any<CancellationToken>());
        await discovery.Received(1).GetDatasetDetailAsync(Payroll, Arg.Any<RequestContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Executor_GetTableSchema_MalformedBatch_IsAStructuredErrorAndReadsNothing()
    {
        var (handler, discovery) = Handler();

        var json = await handler.ExecuteToolAsync(Tool(McpDatasetTools.GetTableSchema), """{"datasets":["sales.public.orders","payroll"]}""", UserSession());

        AiDataGuardrailService.TryGetExecutorError(json, out var code).ShouldBeTrue();
        code.ShouldBe("INVALID_PARAMS");
        await discovery.DidNotReceiveWithAnyArgs().GetDatasetDetailAsync(default, default!, default);
    }

    // ---- 2. Resource reads run through the guardrail ---------------------------------------------------------------------------

    [Theory]
    [InlineData("autheris://catalog/summary", "governance.catalog.datasets")]
    [InlineData("autheris://catalog/datasets/hr.public.payroll/schema", "hr.public.payroll")]
    [InlineData("autheris://catalog/datasources", "governance.catalog.datasources")]
    [InlineData("autheris://governance/my-access", "mcp.tool.describe_my_access")]
    [InlineData("autheris://api/openapi.json", "governance.api.describe")]
    public void NativeResources_MapToTheAbacObjectOfTheirTool(string uri, string table)
    {
        McpResourceProvider.GuardTargets(uri)!.ShouldHaveSingleItem().ToString().ShouldBe(table);
    }

    [Theory]
    [InlineData("autheris://catalog/datasets/payroll/schema")]
    [InlineData("autheris://catalog/datasets//schema")]
    [InlineData("autheris://catalog/unknown")]
    [InlineData("autheris://api/unknown.json")]
    public void UnknownOrInvalidNativeResources_HaveNoTarget(string uri)
    {
        McpResourceProvider.GuardTargets(uri).ShouldBeNull();
    }

    [Fact]
    public async Task ResourceRead_DeniedByCasbin_NeverReadsAndIsAuditedAsDeny()
    {
        var audit = Substitute.For<IAuditLogRepository>();
        var reads = 0;

        var result = await Guardrail(PolicyDenying(Payroll), audit: audit).ReadResourceWithGuardrailAsync(
            "autheris://catalog/datasets/hr.public.payroll/schema",
            [Payroll],
            _ => { reads++; return Task.FromResult("secret schema"); },
            UserSession());

        result.IsSuccess.ShouldBeFalse();
        reads.ShouldBe(0);
        await audit.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == "MCP_TOOL_EXECUTION" && e.Decision == "DENY"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResourceRead_Allowed_IsScrubbedAndAuditedAsAllow()
    {
        var audit = Substitute.For<IAuditLogRepository>();

        var result = await Guardrail(PolicyDenying(), audit: audit).ReadResourceWithGuardrailAsync(
            "autheris://catalog/summary",
            [McpDatasetTools.CatalogTable],
            _ => Task.FromResult("| owner | jane.doe@example.com |"),
            UserSession());

        result.IsSuccess.ShouldBeTrue();
        result.IsMasked.ShouldBeTrue();
        result.ContentJson.ShouldNotContain("jane.doe@example.com");
        await audit.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == "MCP_TOOL_EXECUTION" && e.Decision == "ALLOW"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResourceRead_WithoutTargets_FailsClosed()
    {
        var reads = 0;

        var result = await Guardrail().ReadResourceWithGuardrailAsync(
            "autheris://catalog/summary", [], _ => { reads++; return Task.FromResult("x"); }, UserSession());

        result.IsSuccess.ShouldBeFalse();
        reads.ShouldBe(0);
    }

    [Fact]
    public async Task ResourceRead_OverTheTokenBudget_IsTruncated()
    {
        var result = await Guardrail().ReadResourceWithGuardrailAsync(
            "autheris://catalog/summary", [McpDatasetTools.CatalogTable], _ => Task.FromResult(new string('x', 100_000)), UserSession());

        result.IsSuccess.ShouldBeTrue();
        result.TruncatedDueToBudget.ShouldBeTrue();
        result.ContentJson.Length.ShouldBeLessThanOrEqualTo(4096 * 4);
    }

    private static IServiceProvider ResourceServices(IAiDataGuardrailService guardrail)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", "tenant-a")], "Bearer"))
        };
        http.Items[SecurityPrincipalContext.TenantIdItemKey] = new TenantId("tenant-a");
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(http);

        var services = new ServiceCollection();
        services.AddSingleton(accessor);
        services.AddSingleton(Options.Create(new GatewayOptions { Casbin = new CasbinOptions { Enabled = true }, Mcp = new McpOptions { Enabled = true } }));
        services.AddSingleton(guardrail);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SdkResourceRead_OfADeniedSchema_IsNotFound_AndTheProviderIsNeverCalled()
    {
        var provider = Substitute.For<IMcpResourceProvider>();
        provider.IsResourceEnabled(Arg.Any<string>()).Returns(true);
        var services = ResourceServices(Guardrail(PolicyDenying(Payroll)));

        var ex = await Should.ThrowAsync<McpProtocolException>(async () =>
            await GatewayMcpServer.ReadGuardedNativeResourceAsync(services, "autheris://catalog/datasets/hr.public.payroll/schema", provider, CancellationToken.None));

        ex.ErrorCode.ShouldBe(McpErrorCode.ResourceNotFound);
        await provider.DidNotReceiveWithAnyArgs().ReadAsync(default!, default!, default);
    }

    [Fact]
    public async Task SdkResourceRead_Allowed_ReturnsTheScrubbedProviderContent()
    {
        var provider = Substitute.For<IMcpResourceProvider>();
        provider.IsResourceEnabled(Arg.Any<string>()).Returns(true);
        provider.ReadAsync("autheris://catalog/summary", Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(new McpResourceContent("autheris://catalog/summary", "text/markdown", "owner: jane.doe@example.com"));
        var services = ResourceServices(Guardrail(PolicyDenying()));

        var result = await GatewayMcpServer.ReadGuardedNativeResourceAsync(services, "autheris://catalog/summary", provider, CancellationToken.None);

        var text = ((ModelContextProtocol.Protocol.TextResourceContents)result.Contents[0]).Text;
        text.ShouldNotContain("jane.doe@example.com");
        text.ShouldContain("example.com");
    }

    [Fact]
    public async Task SdkResourceRead_OfAClosedResource_IsNotFound_WithoutTheGuardrail()
    {
        var provider = Substitute.For<IMcpResourceProvider>();
        provider.IsResourceEnabled(Arg.Any<string>()).Returns(false);
        var guardrail = Substitute.For<IAiDataGuardrailService>();
        var services = ResourceServices(guardrail);

        await Should.ThrowAsync<McpProtocolException>(async () =>
            await GatewayMcpServer.ReadGuardedNativeResourceAsync(services, "autheris://catalog/summary", provider, CancellationToken.None));

        guardrail.ReceivedCalls().ShouldBeEmpty();
    }

    // ---- 3. App-only identities carry no group SIDs on any path ----------------------------------------------------------------

    [Fact]
    public void ResolveCaller_AppOnlyToken_DropsTheGroupClaims()
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("client_id", "shared-app"), new Claim("appid", "shared-app"), new Claim(ClaimTypes.GroupSid, "S-1-5-21-GROUP"), new Claim("tenant_id", "tenant-a")],
                "Bearer"))
        };
        http.Items[SecurityPrincipalContext.TenantIdItemKey] = new TenantId("tenant-a");

        var caller = McpEndpoints.ResolveCaller(http, allowOpenMcp: false);

        caller.UserSid.ShouldBeNull();
        caller.GroupSids.ShouldBeEmpty();
    }

    [Fact]
    public void ResolveCaller_UserToken_KeepsTheGroupClaims()
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-CAROL"), new Claim("client_id", "shared-spa"), new Claim(ClaimTypes.GroupSid, "S-1-5-21-GROUP"), new Claim("tenant_id", "tenant-a")],
                "Bearer"))
        };
        http.Items[SecurityPrincipalContext.TenantIdItemKey] = new TenantId("tenant-a");

        var caller = McpEndpoints.ResolveCaller(http, allowOpenMcp: false);

        caller.UserSid.ShouldBe("S-1-5-21-CAROL");
        caller.GroupSids.ShouldContain("S-1-5-21-GROUP");
    }

    [Fact]
    public void QueryExecutorPrincipal_AppOnlySession_IsMarkedAndHasNoGroups()
    {
        var principal = GatewayMcpQueryExecutor.BuildPrincipal(AppOnlySession(["S-1-5-21-GROUP"]));

        principal.FindAll(ClaimTypes.GroupSid).ShouldBeEmpty();
        principal.FindFirst(McpProtocolHandler.IdentityKindClaimType)?.Value.ShouldBe(McpProtocolHandler.ApplicationIdentityKind);
        principal.FindFirst("sub")?.Value.ShouldBe("app-client");
        principal.Identity!.AuthenticationType.ShouldBe("McpAuth");
    }

    [Fact]
    public void QueryExecutorPrincipal_UserSession_KeepsGroupsAndSeparatesTheClient()
    {
        var principal = GatewayMcpQueryExecutor.BuildPrincipal(UserSession(["S-1-5-21-GROUP"]) with
        {
            AdditionalClaims = new Dictionary<string, string> { ["sub"] = "token-sub", [McpProtocolHandler.IdentityKindClaimType] = "user" }
        });

        principal.FindAll(ClaimTypes.GroupSid).Select(c => c.Value).ShouldBe(["S-1-5-21-GROUP"]);
        principal.FindFirst(McpProtocolHandler.IdentityKindClaimType).ShouldBeNull();
        principal.FindAll("sub").Select(c => c.Value).ShouldBe(["S-1-USER"]);
        principal.FindFirst("client_id")?.Value.ShouldBe("agent");
    }

    [Fact]
    public async Task GuardrailAbac_AppOnlySession_IsEvaluatedWithoutGroups()
    {
        var captured = new List<SecurityEvaluationContext>();
        var policy = Substitute.For<IPolicyEnforcementService>();
        policy.EvaluatePolicyAsync(Arg.Do<SecurityEvaluationContext>(captured.Add), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult(TableAccessDecision.Allowed(ci.Arg<SecurityEvaluationContext>().TargetTable, new Dictionary<string, ColumnAccessLevel>())));
        var guardrail = Guardrail(policy, Executor("""{"datasets":[]}"""));

        await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest(McpDatasetTools.ListDatasets, "{}"), AppOnlySession(["S-1-5-21-GROUP"]));
        await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest(McpDatasetTools.ListDatasets, "{}"), UserSession(["S-1-5-21-GROUP"]));

        captured.Count.ShouldBe(2);
        captured[0].UserSid.Value.ShouldBe("app-client");
        captured[0].GroupSids.ShouldBeEmpty();
        captured[1].GroupSids.Select(g => g.Value).ShouldBe(["S-1-5-21-GROUP"]);
    }

    // ---- 4. Mutating admin tools: two-phase confirmation, audit row, fixed errors ----------------------------------------------

    private static McpSessionContext AdminSession => new(
        "s1", "sp1", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: AdminSid, Roles: ["ClusterAdmin"]);

    [Theory]
    [InlineData(McpAdminTools.SetDatasetState, """{"datasetId":"sales.public.orders","state":"quarantined"}""")]
    [InlineData(McpAdminTools.RegisterDatasource, """{"name":"crm","domain":"sales","baseUrl":"https://crm.example"}""")]
    public async Task MutatingAdminTool_WithoutConfirmation_OnlyPlans(string tool, string args)
    {
        var planning = Substitute.For<IAccessPlanningService>();
        planning.PlanAdminOperationAsync(default!, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(new AdminPlanAccessResult("plan_1", "t", [], [], DateTimeOffset.UtcNow.AddMinutes(10)));

        var result = await McpAdminTools.ExecuteAdminToolAsync(tool, args, AdminSession, planning);

        result.IsSuccess.ShouldBeTrue();
        result.ContentJson.ShouldContain("CONFIRMATION_REQUIRED");
        await planning.DidNotReceiveWithAnyArgs().SetDatasetStateAsync(default!, default!, default, default);
        await planning.DidNotReceiveWithAnyArgs().RegisterDatasourceAsync(default!, default!, default, default);
        await planning.Received(1).PlanAdminOperationAsync(tool, Arg.Any<string>(), Arg.Any<string>(), AdminSid, "tenant-a", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MutatingAdminTool_OnlyOneConfirmationProperty_IsRejected()
    {
        var planning = Substitute.For<IAccessPlanningService>();

        var result = await McpAdminTools.ExecuteAdminToolAsync(McpAdminTools.SetDatasetState,
            """{"datasetId":"sales.public.orders","state":"quarantined","planId":"plan_1"}""", AdminSession, planning);

        result.IsSuccess.ShouldBeFalse();
        await planning.DidNotReceiveWithAnyArgs().SetDatasetStateAsync(default!, default!, default, default);
        await planning.DidNotReceiveWithAnyArgs().PlanAdminOperationAsync(default!, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task MutatingAdminTool_UnexpectedFailure_ReturnsAFixedMessage()
    {
        var planning = Substitute.For<IAccessPlanningService>();
        planning.SetDatasetStateAsync(default!, default!, default, default)
            .ReturnsForAnyArgs<Task<AdminSetDatasetStateResult>>(_ => throw new InvalidOperationException("Server=db-prod-01;Password=hunter2"));

        var result = await McpAdminTools.ExecuteAdminToolAsync(McpAdminTools.SetDatasetState,
            """{"datasetId":"sales.public.orders","state":"quarantined","planId":"plan_1","confirmationToken":"tok"}""", AdminSession, planning);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("Admin tool 'admin_set_dataset_state' failed.");
    }

    [Fact]
    public async Task MutatingAdminTool_InvalidArguments_ReturnAFixedMessage()
    {
        var result = await McpAdminTools.ExecuteAdminToolAsync(McpAdminTools.SetDatasetState,
            """{"datasetId":"sales.public.orders","state":{"x":"secret-value"}}""", AdminSession, Substitute.For<IAccessPlanningService>());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldNotContain("secret-value");
        result.ErrorMessage.ShouldBe("Invalid arguments for admin tool 'admin_set_dataset_state'.");
    }

    [Fact]
    public async Task MutatingAdminTool_EndToEnd_RunsOnlyWithTheTotpConfirmedTokenForTheSameArguments()
    {
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(new TableMetadata { Identifier = Orders, Table = new Table { TableName = "orders", SchemaName = "public", IsActive = true } });
        ITotpSecretStore secrets = new InMemoryTotpSecretStore();
        ITotpVerificationService totp = new TotpVerificationService();
        var service = new AccessPlanningService(Substitute.For<IAuditLogRepository>(), tableRepo, Substitute.For<IRebacStore>(), totp, secrets);
        const string args = """{"datasetId":"sales.public.orders","state":"quarantined","reason":"drift"}""";

        // Phase 1: plan only.
        var planned = await McpAdminTools.ExecuteAdminToolAsync(McpAdminTools.SetDatasetState, args, AdminSession, service);
        planned.IsSuccess.ShouldBeTrue();
        using var plannedDoc = JsonDocument.Parse(planned.ContentJson);
        var planId = plannedDoc.RootElement.GetProperty("planId").GetString()!;
        await tableRepo.DidNotReceiveWithAnyArgs().UpsertTableMetadataAsync(default!);

        // Confirmation with TOTP (the REST confirm endpoint calls the same method).
        var enrollment = totp.GenerateEnrollment(AdminSid, "admin@autheris.local");
        await secrets.SetSecretAsync(AdminSid, enrollment.SecretBase32);
        var token = (await service.ConfirmPlanAsync(planId, totp.GenerateTotpCode(enrollment.SecretBase32), AdminSid)).ConfirmationToken;

        // The token never applies other arguments (and a rejected attempt does not burn it).
        var tampered = await McpAdminTools.ExecuteAdminToolAsync(McpAdminTools.SetDatasetState,
            JsonSerializer.Serialize(new { datasetId = "sales.public.orders", state = "active", reason = "drift", planId, confirmationToken = token }), AdminSession, service);
        tampered.IsSuccess.ShouldBeFalse();
        await tableRepo.DidNotReceiveWithAnyArgs().UpsertTableMetadataAsync(default!);

        // Nor another operation.
        await Should.ThrowAsync<SecurityException>(() => service.ApplyAccessAsync(new AdminApplyAccessRequest(planId, token), AdminSid, "tenant-a"));

        // Phase 2: the same arguments with the confirmed token run once.
        var confirmed = JsonSerializer.Serialize(new { datasetId = "sales.public.orders", state = "quarantined", reason = "drift", planId, confirmationToken = token });
        var applied = await McpAdminTools.ExecuteAdminToolAsync(McpAdminTools.SetDatasetState, confirmed, AdminSession, service);
        applied.IsSuccess.ShouldBeTrue(applied.ErrorMessage);
        await tableRepo.Received(1).UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());

        var replay = await McpAdminTools.ExecuteAdminToolAsync(McpAdminTools.SetDatasetState, confirmed, AdminSession, service);
        replay.IsSuccess.ShouldBeFalse();
        await tableRepo.Received(1).UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AdminToolCall_WritesAnAllowAuditRow()
    {
        var audit = Substitute.For<IAuditLogRepository>();
        var planning = Substitute.For<IAccessPlanningService>();
        planning.PlanAdminOperationAsync(default!, default!, default!, default!, default, default)
            .ReturnsForAnyArgs(new AdminPlanAccessResult("plan_1", "t", [], [], DateTimeOffset.UtcNow.AddMinutes(10)));

        var result = await Guardrail(audit: audit, planning: planning).ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpAdminTools.SetDatasetState, """{"datasetId":"sales.public.orders","state":"quarantined"}"""), AdminSession);

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
        await audit.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == "MCP_TOOL_EXECUTION" && e.Decision == "ALLOW" && e.TargetTable == McpAdminTools.SetDatasetState),
            Arg.Any<CancellationToken>());
    }

    // ---- 5. Structured tool errors and the catalog search limit ----------------------------------------------------------------

    [Theory]
    [InlineData("""{"error":"InvalidParams","message":"Missing required argument 'query'."}""", "InvalidParams")]
    [InlineData("""{"error":"Doc search service is not configured."}""", "ERROR")]
    [InlineData("""{"isError":true,"error":{"code":"FORBIDDEN","message":"x"}}""", "FORBIDDEN")]
    public void TryGetExecutorError_RecognisesBothErrorShapes(string json, string code)
    {
        AiDataGuardrailService.TryGetExecutorError(json, out var errorCode).ShouldBeTrue();
        errorCode.ShouldBe(code);
    }

    [Theory]
    [InlineData("""{"error":"x","rows":[]}""")]
    [InlineData("""{"dataset":"a.b.c","allowed":false,"message":"Access denied or dataset not found."}""")]
    [InlineData("""{"error":null,"message":"m"}""")]
    public void TryGetExecutorError_IgnoresDataResults(string json)
    {
        AiDataGuardrailService.TryGetExecutorError(json, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task InvalidParams_AreAuditedAsDeny_AndReturnedAsToolErrors()
    {
        var audit = Substitute.For<IAuditLogRepository>();
        var executor = Executor("""{"tenantId":"tenant-a","tool":"query_dataset","isError":true,"error":{"code":"INVALID_PARAMS","message":"Missing or invalid required argument 'dataset'."}}""");

        var result = await Guardrail(audit: audit, executor: executor).ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(McpDatasetTools.GetTableSchema, "{}"), UserSession());

        result.IsSuccess.ShouldBeFalse();
        await audit.Received(1).RecordAuditEventAsync(Arg.Is<AuditLogEntry>(e => e.Decision == "DENY"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(100000, 20)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(7, 7)]
    public async Task SearchCatalog_Limit_IsClamped(int requested, int expected)
    {
        var (handler, discovery) = Handler();

        await handler.ExecuteToolAsync(Tool(McpDatasetTools.SearchCatalog), JsonSerializer.Serialize(new { query = "orders", limit = requested }), UserSession());

        await discovery.Received(1).SearchCatalogDetailedAsync(Arg.Is<CatalogSearchQuery>(q => q.Limit == expected), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(McpDatasetTools.SearchCatalog, "query")]
    [InlineData(McpDatasetTools.FindRelevantTables, "use_case")]
    public async Task CatalogSearch_TooLongQuery_IsAnInvalidParamsToolError_WithoutEcho(string tool, string argument)
    {
        var (handler, discovery) = Handler();
        discovery.SearchCatalogDetailedAsync(Arg.Any<CatalogSearchQuery>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<CatalogSearchHit>>>(_ => throw new Autheris.Domain.Exceptions.GatewayInvalidQueryException("query too long", "CATALOG_SEARCH_QUERY_TOO_LONG"));
        var marker = "needle-" + new string('q', 64);

        var json = await handler.ExecuteToolAsync(Tool(tool), JsonSerializer.Serialize(new Dictionary<string, string> { [argument] = marker }), UserSession());

        AiDataGuardrailService.TryGetExecutorError(json, out var code).ShouldBeTrue();
        code.ShouldBe("INVALID_PARAMS");
        json.ShouldNotContain("needle-");
    }
}
