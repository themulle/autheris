namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Mcp;
using HotChocolate.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// The MCP dataset tools are registered without demo data, run ABAC on the requested table, require four-eyes
/// approval only for row reads, and are executed by the MCP query executor.
/// </summary>
public sealed class McpDatasetToolWiringTests
{
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");

    private static McpToolDefinition Tool(string name) =>
        new McpToolRegistry(Options.Create(new GatewayOptions()), new FixedDemoData(false)).FindTool(name)
        ?? throw new InvalidOperationException($"Tool '{name}' is not registered.");

    private sealed class FixedDemoData(bool enabled) : IDemoDataSwitch
    {
        public bool Enabled => enabled;
    }

    [Theory]
    [InlineData("list_datasets")]
    [InlineData("describe_dataset")]
    [InlineData("sample_rows")]
    [InlineData("query_graphql")]
    public void DatasetTools_AreRegistered_WithoutDemoData(string name)
    {
        var tool = Tool(name);

        tool.Description.ShouldNotBeNullOrWhiteSpace();
        using var schema = JsonDocument.Parse(tool.InputJsonSchema);
        schema.RootElement.GetProperty("type").GetString().ShouldBe("object");
        if (name is "describe_dataset" or "sample_rows")
        {
            schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldContain("dataset");
        }
    }

    [Theory]
    [InlineData("describe_dataset")]
    [InlineData("sample_rows")]
    public void DatasetTools_ResolveTheRequestedTable(string name)
    {
        var tables = AiDataGuardrailService.ParseTablesFromTool(Tool(name), """{"dataset":"Sales.Public.Orders"}""");

        tables.ShouldNotBeNull();
        tables.ShouldHaveSingleItem().ShouldBe(Orders);
    }

    [Theory]
    [InlineData("describe_dataset", "{}")]
    [InlineData("sample_rows", """{"dataset":"orders"}""")]
    [InlineData("sample_rows", """{"dataset":42}""")]
    [InlineData("sample_rows", "not json")]
    [InlineData("sample_rows", """{"dataset":"sales.public.orders","Dataset":"hr.public.payroll"}""")]
    [InlineData("sample_rows", """{"dataset":"sales.public.orders","dataset":"hr.public.payroll"}""")]
    public void DatasetTools_WithoutAValidDataset_FailClosed(string name, string args)
    {
        AiDataGuardrailService.ParseTablesFromTool(Tool(name), args).ShouldBeNull();
    }

    [Fact]
    public void ListDatasets_IsCheckedAsACatalogTool()
    {
        AiDataGuardrailService.ParseTablesFromTool(Tool("list_datasets"), "{}")!
            .ShouldHaveSingleItem().ShouldBe(new TableIdentifier("governance", "catalog", "datasets"));
    }

    [Theory]
    [InlineData("describe_dataset", true)]
    [InlineData("sample_rows", false)]
    public async Task FourEyesTable_BlocksRowReads_ButNotTheDescription(string name, bool allowed)
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>())
            .Returns(new TableMetadata { Identifier = Orders, Table = new Table { TableName = "orders", RequiresFourEyes = true } });
        var executor = Substitute.For<IMcpQueryExecutor>();
        executor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("""{"id":"sales.public.orders"}"""));

        var guardrail = new AiDataGuardrailService(
            new McpToolRegistry(Options.Create(new GatewayOptions()), new FixedDemoData(false)),
            Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } }),
            NullLogger<AiDataGuardrailService>.Instance,
            queryExecutor: executor,
            tableMetadataRepository: metadataRepo);

        var session = new McpSessionContext("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER");
        var result = await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest(name, """{"dataset":"sales.public.orders"}"""), session);

        result.IsSuccess.ShouldBe(allowed);
        if (!allowed)
        {
            result.ErrorMessage!.ShouldContain("Four-Eyes");
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Guardrail_AsksCasbin_OnlyWhenCasbinIsEnabled(bool casbinEnabled, bool expectedSuccess)
    {
        // With Casbin disabled (the default) the engine has no policies and would deny every tool call.
        var policy = Substitute.For<IPolicyEnforcementService>();
        policy.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult(TableAccessDecision.Denied(McpDatasetTools.CatalogTable, "no policy")));
        var executor = Substitute.For<IMcpQueryExecutor>();
        executor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("""{"datasets":[]}"""));

        var guardrail = new AiDataGuardrailService(
            new McpToolRegistry(Options.Create(new GatewayOptions()), new FixedDemoData(false)),
            Options.Create(new GatewayOptions { Casbin = new CasbinOptions { Enabled = casbinEnabled }, Mcp = new McpOptions { Enabled = true } }),
            NullLogger<AiDataGuardrailService>.Instance,
            queryExecutor: executor,
            policyEnforcementService: policy);

        var session = new McpSessionContext("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER");
        var result = await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest("list_datasets", "{}"), session);

        result.IsSuccess.ShouldBe(expectedSuccess);
        await policy.Received(casbinEnabled ? 1 : 0).EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>());
    }

    private static (AiDataGuardrailService Guardrail, IPolicyEnforcementService Policy, IMcpQueryExecutor Executor) GraphQlGuardrail(
        IReadOnlyList<TableIdentifier>? documentTables, bool fourEyes = false)
    {
        var map = Substitute.For<IGraphQlCatalogMap>();
        map.ResolveDocumentTablesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(documentTables));
        var policy = Substitute.For<IPolicyEnforcementService>();
        policy.EvaluatePolicyAsync(Arg.Any<SecurityEvaluationContext>(), Arg.Any<CancellationToken>())
            .Returns(c => ValueTask.FromResult(TableAccessDecision.Allowed(c.Arg<SecurityEvaluationContext>().TargetTable, new Dictionary<string, ColumnAccessLevel>())));
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(c => new TableMetadata { Identifier = c.Arg<TableIdentifier>(), Table = new Table { RequiresFourEyes = fourEyes } });
        var executor = Substitute.For<IMcpQueryExecutor>();
        executor.ExecuteOperationAsync(Arg.Any<McpToolDefinition>(), Arg.Any<string>(), Arg.Any<McpSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("""{"data":{}}"""));

        var guardrail = new AiDataGuardrailService(
            new McpToolRegistry(Options.Create(new GatewayOptions()), new FixedDemoData(false)),
            Options.Create(new GatewayOptions { Casbin = new CasbinOptions { Enabled = true }, Mcp = new McpOptions { Enabled = true } }),
            NullLogger<AiDataGuardrailService>.Instance,
            queryExecutor: executor,
            policyEnforcementService: policy,
            tableMetadataRepository: metadataRepo,
            graphQlCatalogMap: map);
        return (guardrail, policy, executor);
    }

    private static ValueTask<McpToolCallResult> CallGraphQl(AiDataGuardrailService guardrail, string args) =>
        guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("query_graphql", args),
            new McpSessionContext("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER"));

    [Fact]
    public async Task QueryGraphQl_RunsAbacOnEveryTableOfTheDocument()
    {
        var customers = new TableIdentifier("crm", "dbo", "customers");
        var (guardrail, policy, _) = GraphQlGuardrail([Orders, customers]);

        var result = await CallGraphQl(guardrail, """{"query":"{ sales_public_orders { id } }"}""");

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
        await policy.Received(1).EvaluatePolicyAsync(Arg.Is<SecurityEvaluationContext>(c => c.TargetTable == Orders), Arg.Any<CancellationToken>());
        await policy.Received(1).EvaluatePolicyAsync(Arg.Is<SecurityEvaluationContext>(c => c.TargetTable == customers), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task QueryGraphQl_FourEyesTable_NeedsApproval()
    {
        var (guardrail, _, executor) = GraphQlGuardrail([Orders], fourEyes: true);

        var result = await CallGraphQl(guardrail, """{"query":"{ sales_public_orders { id } }"}""");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("Four-Eyes");
        await executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
    }

    [Theory]
    [InlineData("""{"query":"{ x }"}""")]
    [InlineData("""{}""")]
    [InlineData("""{"query":"{ a }","Query":"{ b }"}""")]
    public async Task QueryGraphQl_UnresolvableDocument_FailsClosed(string args)
    {
        var (guardrail, policy, executor) = GraphQlGuardrail(null);

        var result = await CallGraphQl(guardrail, args);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("fail-closed");
        await executor.DidNotReceiveWithAnyArgs().ExecuteOperationAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task QueryGraphQl_IntrospectionOnly_IsCheckedAsACatalogCall()
    {
        var (guardrail, policy, _) = GraphQlGuardrail([]);

        var result = await CallGraphQl(guardrail, """{"query":"{ __schema { types { name } } }"}""");

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
        await policy.Received(1).EvaluatePolicyAsync(Arg.Is<SecurityEvaluationContext>(c => c.TargetTable == McpDatasetTools.CatalogTable), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"query":"mutation { revoke { ok } }"}""", "Only queries")]
    [InlineData("""{"query":"subscription { changes { id } }"}""", "Only queries")]
    [InlineData("""{"query":"query A { a } query B { b }"}""", "one operation")]
    [InlineData("""{"query":"{ broken "}""", "parse")]
    [InlineData("""{"variables":{}}""", "query")]
    [InlineData("""{"query":"{ a }","variables":[1]}""", "variables")]
    public async Task Executor_QueryGraphQl_RejectsAnythingButOneQuery(string args, string expected)
    {
        var provider = Substitute.For<IRequestExecutorProvider>();
        var executor = new GatewayMcpQueryExecutor(provider, Substitute.For<IGatewayExecutionService>(), NullLogger<GatewayMcpQueryExecutor>.Instance,
            datasetCatalog: Substitute.For<IMcpDatasetCatalog>());

        var json = await executor.ExecuteOperationAsync(Tool("query_graphql"), args, Session());

        json.ShouldContain("INVALID_PARAMS");
        json.ShouldContain(expected, Case.Insensitive);
        await provider.DidNotReceiveWithAnyArgs().GetExecutorAsync(default, default);
    }

    [Fact]
    public async Task Initialize_TellsAgentsToQueryDataWithGraphQl()
    {
        var registry = new McpToolRegistry(Options.Create(new GatewayOptions()), new FixedDemoData(false));
        var guardrail = new AiDataGuardrailService(registry, Options.Create(new GatewayOptions()), NullLogger<AiDataGuardrailService>.Instance);
        var handler = new McpProtocolHandler(new McpSessionStore(NullLogger<McpSessionStore>.Instance), registry, guardrail, NullLogger<McpProtocolHandler>.Instance);
        var session = handler.CreateSession("agent", "tenant-a");

        var response = await handler.HandleMessageAsync(session.SessionId, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");

        using var doc = JsonDocument.Parse(response);
        var instructions = doc.RootElement.GetProperty("result").GetProperty("instructions").GetString();
        instructions.ShouldNotBeNull();
        instructions.ShouldContain("query_graphql");
        instructions.ShouldContain("list_datasets");
        instructions.ShouldContain("describe_dataset");
    }

    private static GatewayMcpQueryExecutor Executor(IMcpDatasetCatalog catalog) => new(
        Substitute.For<IRequestExecutorProvider>(),
        Substitute.For<IGatewayExecutionService>(),
        NullLogger<GatewayMcpQueryExecutor>.Instance,
        datasetCatalog: catalog);

    private static McpSessionContext Session() =>
        new("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER", Roles: ["Analyst"]);

    [Fact]
    public async Task Executor_DescribeDataset_ReturnsTheDescriptionAsCamelCaseJson()
    {
        var catalog = Substitute.For<IMcpDatasetCatalog>();
        catalog.DescribeDatasetAsync(Arg.Any<ClaimsPrincipal>(), "sales.public.orders", Arg.Any<CancellationToken>())
            .Returns(new McpDatasetDescription("sales.public.orders", "sales", "public", "orders", "Orders", null, "INTERNAL", false,
                [new McpDatasetColumn("id", "int", "Key", false, true)], []));

        var json = await Executor(catalog).ExecuteOperationAsync(Tool("describe_dataset"), """{"dataset":"sales.public.orders"}""", Session());

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("id").GetString().ShouldBe("sales.public.orders");
        doc.RootElement.GetProperty("columns")[0].GetProperty("primaryKey").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Executor_SampleRows_PassesTheCallerAndTheCount()
    {
        var catalog = Substitute.For<IMcpDatasetCatalog>();
        ClaimsPrincipal? caller = null;
        catalog.SampleRowsAsync(Arg.Do<ClaimsPrincipal>(p => caller = p), "sales.public.orders", 3, Arg.Any<CancellationToken>())
            .Returns(new McpDatasetSample("sales.public.orders", 0, []));

        var json = await Executor(catalog).ExecuteOperationAsync(Tool("sample_rows"), """{"dataset":"sales.public.orders","count":3}""", Session());

        json.ShouldContain("\"rowCount\":0");
        caller.ShouldNotBeNull();
        caller.GetUserSid()!.Value.Value.ShouldBe("S-1-USER");
        caller.GetTenantId().Value.ShouldBe("tenant-a");
    }

    [Fact]
    public async Task Executor_ListDatasets_PassesSearchAndDomain()
    {
        var catalog = Substitute.For<IMcpDatasetCatalog>();
        catalog.ListDatasetsAsync(Arg.Any<ClaimsPrincipal>(), "order", "sales", Arg.Any<CancellationToken>())
            .Returns(new McpDatasetList([new McpDatasetSummary("sales.public.orders", "Orders", "INTERNAL", 3)], 1, false));

        var json = await Executor(catalog).ExecuteOperationAsync(Tool("list_datasets"), """{"search":"order","domain":"sales"}""", Session());

        json.ShouldContain("sales.public.orders");
    }

    [Fact]
    public async Task Executor_HiddenOrMissingDataset_IsReportedAsNotFound()
    {
        var catalog = Substitute.For<IMcpDatasetCatalog>();
        catalog.DescribeDatasetAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<McpDatasetDescription>(_ => throw new TableNotFoundException(Orders));

        var json = await Executor(catalog).ExecuteOperationAsync(Tool("describe_dataset"), """{"dataset":"sales.public.orders"}""", Session());

        json.ShouldContain("\"isError\":true");
        json.ShouldContain("NOT_FOUND");
        json.ShouldNotContain("does not exist");
    }

    [Fact]
    public async Task Executor_InvalidDatasetId_IsAnInvalidParamsError()
    {
        var catalog = Substitute.For<IMcpDatasetCatalog>();
        catalog.SampleRowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns<McpDatasetSample>(_ => throw new ArgumentException("The dataset id must have the form 'domain.schema.table'."));

        var json = await Executor(catalog).ExecuteOperationAsync(Tool("sample_rows"), """{"dataset":"orders"}""", Session());

        json.ShouldContain("INVALID_PARAMS");
        json.ShouldContain("domain.schema.table");
    }

    [Fact]
    public async Task Executor_DeniedRead_IsForbidden()
    {
        var catalog = Substitute.For<IMcpDatasetCatalog>();
        catalog.SampleRowsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns<McpDatasetSample>(_ => throw new GatewayForbiddenException());

        var json = await Executor(catalog).ExecuteOperationAsync(Tool("sample_rows"), """{"dataset":"sales.public.orders"}""", Session());

        json.ShouldContain("FORBIDDEN");
    }
}
