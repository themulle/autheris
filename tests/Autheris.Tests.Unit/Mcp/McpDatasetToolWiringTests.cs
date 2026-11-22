namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Mcp;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestSupport;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
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
        if (name is "sample_rows")
        {
            schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ShouldContain("dataset");
        }
        else if (name is "describe_dataset")
        {
            var props = schema.RootElement.GetProperty("properties");
            props.TryGetProperty("dataset", out _).ShouldBeTrue();
            props.TryGetProperty("graphQlField", out _).ShouldBeTrue();
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

    private static readonly IRequestExecutorProvider PassiveExecutorProvider = Substitute.For<IRequestExecutorProvider>();

    [Theory]
    [InlineData("describe_dataset", true)]
    [InlineData("sample_rows", false)]
    public async Task FourEyesTable_BlocksRowReads_ButNotTheDescription(string name, bool allowed)
    {
        var metadataRepo = new InMemoryTableMetadataRepository(new TableMetadata
        {
            Identifier = Orders,
            Table = new Table { TableName = "orders" },
            Columns = [new TableColumn { ColumnName = "email" }],
            ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor("email")
        });
        var executor = new StubMcpQueryExecutor("""{"id":"sales.public.orders"}""");

        var guardrail = new AiDataGuardrailService(
            new McpToolRegistry(Options.Create(new GatewayOptions()), new FixedDemoData(false)),
            Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } }),
            NullLogger<AiDataGuardrailService>.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, queryExecutor: executor,
            tableMetadataRepository: metadataRepo, decisionPoint: PdpTestDoubles.AllowAll());

        var session = new McpSessionContext("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER");
        var result = await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest(name, """{"dataset":"sales.public.orders"}"""), session);

        result.IsSuccess.ShouldBe(allowed);
        if (!allowed)
        {
            result.ErrorMessage!.ShouldContain("Four-Eyes");
        }
    }

    private static (AiDataGuardrailService Guardrail, List<Autheris.Application.Policy.Pdp.AccessRequest> PolicyCalls, StubMcpQueryExecutor Executor) GraphQlGuardrail(
        IReadOnlyList<TableIdentifier>? documentTables, bool fourEyes = false)
    {
        var map = new StubGraphQlCatalogMap(documentTables);
        var policyCalls = new List<Autheris.Application.Policy.Pdp.AccessRequest>();
        var policy = AccessDecisionPointStubs.Returning(r =>
        {
            policyCalls.Add(r);
            return TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>());
        });
        var metadataRepo = new AnyTableMetadataRepo(fourEyes);
        var executor = new StubMcpQueryExecutor("""{"data":{}}""");

        var guardrail = new AiDataGuardrailService(
            new McpToolRegistry(Options.Create(new GatewayOptions()), new FixedDemoData(false)),
            Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } }),
            NullLogger<AiDataGuardrailService>.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, queryExecutor: executor,
            decisionPoint: policy,
            tableMetadataRepository: metadataRepo,
            graphQlCatalogMap: map);
        return (guardrail, policyCalls, executor);
    }

    private static ValueTask<McpToolCallResult> CallGraphQl(AiDataGuardrailService guardrail, string args) =>
        guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest("query_graphql", args),
            new McpSessionContext("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER"));

    [Fact]
    public async Task QueryGraphQl_RunsAbacOnEveryTableOfTheDocument()
    {
        var customers = new TableIdentifier("crm", "dbo", "customers");
        var (guardrail, policyCalls, _) = GraphQlGuardrail([Orders, customers]);

        var result = await CallGraphQl(guardrail, """{"query":"{ sales_public_orders { id } }"}""");

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
        policyCalls.Count(c => c.Object.Identifier == Orders).ShouldBe(1);
        policyCalls.Count(c => c.Object.Identifier == customers).ShouldBe(1);
    }

    [Fact]
    public async Task QueryGraphQl_FourEyesTable_NeedsApproval()
    {
        var (guardrail, _, executor) = GraphQlGuardrail([Orders], fourEyes: true);

        var result = await CallGraphQl(guardrail, """{"query":"{ sales_public_orders { id } }"}""");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("Four-Eyes");
        executor.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"query":"{ x }"}""")]
    [InlineData("""{}""")]
    [InlineData("""{"query":"{ a }","Query":"{ b }"}""")]
    public async Task QueryGraphQl_UnresolvableDocument_FailsClosed(string args)
    {
        var (guardrail, _, executor) = GraphQlGuardrail(null);

        var result = await CallGraphQl(guardrail, args);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("fail-closed");
        executor.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task QueryGraphQl_IntrospectionOnly_IsACatalogCall_WithoutADataDecision()
    {
        var (guardrail, policyCalls, _) = GraphQlGuardrail([]);

        var result = await CallGraphQl(guardrail, """{"query":"{ __schema { types { name } } }"}""");

        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
        // The catalog pseudo target is not a data table: no decision point request (the catalog is filtered by discovery visibility).
        policyCalls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"query":"mutation { revoke { ok } }"}""", "Only queries")]
    [InlineData("""{"query":"subscription { changes { id } }"}""", "Only queries")]
    [InlineData("""{"query":"query A { a } query B { b }"}""", "one operation")]
    [InlineData("""{"query":"{ broken "}""", "parse")]
    [InlineData("""{"variables":{}}""", "query")]
    [InlineData("""{"query":"{ a }","variables":[1]}""", "invalid_arguments")]
    public async Task Executor_QueryGraphQl_RejectsAnythingButOneQuery(string args, string expected)
    {
        var provider = Substitute.For<IRequestExecutorProvider>();
        var executor = new GatewayMcpQueryExecutor(provider, new StubGatewayExecutionService(), NullLogger<GatewayMcpQueryExecutor>.Instance, Autheris.Tests.Unit.Mcp.McpRowCapTests.Options(100),
            datasetCatalog: StubMcpDatasetCatalog.Empty, persistedToolValidator: new Autheris.Application.Mcp.Services.PersistedToolValidator());

        var json = await executor.ExecuteOperationAsync(Tool("query_graphql"), args, Session());

        json.ShouldContain("INVALID_PARAMS");
        json.ShouldContain(expected, Case.Insensitive);
        await provider.DidNotReceiveWithAnyArgs().GetExecutorAsync(default, default);
    }

    [Fact]
    public async Task Executor_QueryGraphQl_WithVariables_AugmentsDefaultValues()
    {
        var provider = Substitute.For<IRequestExecutorProvider>();
        var requestExecutor = Substitute.For<IRequestExecutor>();
        provider.GetExecutorAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(requestExecutor);

        IOperationRequest? capturedRequest = null;
        requestExecutor.ExecuteAsync(Arg.Do<IOperationRequest>(r => capturedRequest = r), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IExecutionResult>(OperationResult.FromError(ErrorBuilder.New().SetMessage("success").Build())));

        var executor = new GatewayMcpQueryExecutor(provider, new StubGatewayExecutionService(), NullLogger<GatewayMcpQueryExecutor>.Instance, Autheris.Tests.Unit.Mcp.McpRowCapTests.Options(100),
            datasetCatalog: StubMcpDatasetCatalog.Empty, persistedToolValidator: new Autheris.Application.Mcp.Services.PersistedToolValidator());

        var args = """
        {
            "query": "query GetInvoices($limit: Int) { finance_invoices(first: $limit) { id } }",
            "variables": { "limit": 5 }
        }
        """;

        await executor.ExecuteOperationAsync(Tool("query_graphql"), args, Session());

        capturedRequest.ShouldNotBeNull();
        capturedRequest.Document.ShouldNotBeNull();
        var nodeProvider = capturedRequest.Document.ShouldBeAssignableTo<IOperationDocumentNodeProvider>();
        var docNode = nodeProvider.Document;
        docNode.ShouldNotBeNull();
        var op = docNode.Definitions.OfType<OperationDefinitionNode>().Single();
        var limitVar = op.VariableDefinitions.Single(v => v.Variable.Name.Value == "limit");
        limitVar.DefaultValue.ShouldNotBeNull();
        ((IntValueNode)limitVar.DefaultValue).ToInt32().ShouldBe(5);
    }

    [Fact]
    public void Initialize_TellsAgentsToQueryDataWithGraphQl()
    {
        var instructions = McpDatasetTools.BuildServerInstructions(ReadPathSwitches.From(new GatewayOptions()));

        instructions.ShouldNotBeNull();
        instructions.ShouldContain("query_graphql");
        instructions.ShouldContain("list_datasets");
        instructions.ShouldContain("describe_dataset");
    }

    private static GatewayMcpQueryExecutor Executor(IMcpDatasetCatalog catalog, IGatewayExecutionService? gateway = null) => new(
        PassiveExecutorProvider,
        gateway ?? new StubGatewayExecutionService(),
        NullLogger<GatewayMcpQueryExecutor>.Instance, Autheris.Tests.Unit.Mcp.McpRowCapTests.Options(100),
        datasetCatalog: catalog,
        persistedToolValidator: new Autheris.Application.Mcp.Services.PersistedToolValidator());

    private static McpSessionContext Session() =>
        new("s1", "agent", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER", Roles: ["Analyst"]);

    [Fact]
    public async Task Executor_DescribeDataset_ReturnsTheDescriptionAsCamelCaseJson()
    {
        var catalog = new StubMcpDatasetCatalog
        {
            OnDescribe = (_, ds) => new McpDatasetDescription(ds, "orders", "sales", "public", "orders", "Orders", null, "INTERNAL", false,
                [new McpDatasetColumn("id", "int", "Key", false, true)], [])
        };

        var json = await Executor(catalog).ExecuteOperationAsync(Tool("describe_dataset"), """{"dataset":"sales.public.orders"}""", Session());

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("id").GetString().ShouldBe("sales.public.orders");
        doc.RootElement.GetProperty("columns")[0].GetProperty("primaryKey").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Executor_SampleRows_PassesTheCallerAndTheCount()
    {
        var catalog = new StubMcpDatasetCatalog
        {
            OnSample = (_, ds, _) => new McpDatasetSample(ds, 0, [])
        };

        var json = await Executor(catalog).ExecuteOperationAsync(Tool("sample_rows"), """{"dataset":"sales.public.orders","count":3}""", Session());

        json.ShouldContain("\"rowCount\":0");
        catalog.SampleCalls.Count.ShouldBe(1);
        var (caller, ds, count) = catalog.SampleCalls[0];
        ds.ShouldBe("sales.public.orders");
        count.ShouldBe(3);
        caller.GetUserSid()!.Value.Value.ShouldBe("S-1-USER");
        caller.GetTenantId().Value.ShouldBe("tenant-a");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SampleRows_NoSyntheticArgument_ExecutesGovernedReadOnly()
    {
        var tool = Tool("sample_rows");
        tool.InputJsonSchema.Contains("\"synthetic\"", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();

        var catalog = new StubMcpDatasetCatalog
        {
            OnSample = (_, ds, _) => new McpDatasetSample(ds, 0, [], IsSynthetic: false)
        };
        var gateway = new StubGatewayExecutionService();
        var executor = Executor(catalog, gateway);

        var json = await executor.ExecuteOperationAsync(tool, """{"dataset":"sales.public.orders","count":3}""", Session());

        json.ShouldContain("\"isSynthetic\":false");
        catalog.SampleCalls.Count.ShouldBe(1);
        catalog.SampleCalls[0].Dataset.ShouldBe("sales.public.orders");
        catalog.SampleCalls[0].Count.ShouldBe(3);
        gateway.PageCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Executor_ListDatasets_PassesSearchAndDomain()
    {
        var catalog = new StubMcpDatasetCatalog
        {
            OnList = (_, search, domain) => search == "order" && domain == "sales"
                ? new McpDatasetList([new McpDatasetSummary("sales.public.orders", "orders", "Orders", "INTERNAL", 3)], 1, false)
                : new McpDatasetList([], 0, false)
        };

        var json = await Executor(catalog).ExecuteOperationAsync(Tool("list_datasets"), """{"search":"order","domain":"sales"}""", Session());

        json.ShouldContain("sales.public.orders");
    }

    [Fact]
    public async Task ListDatasets_Json_HasCamelCaseDisplayNameAfterId()
    {
        var catalog = new StubMcpDatasetCatalog
        {
            OnList = (_, _, _) => new McpDatasetList([new McpDatasetSummary("sales.public.orders", "Aufträge", "Orders", "INTERNAL", 3)], 1, false)
        };

        var json = await Executor(catalog).ExecuteOperationAsync(Tool("list_datasets"), "{}", Session());

        using var doc = JsonDocument.Parse(json);
        var row = doc.RootElement.GetProperty("datasets")[0];
        row.EnumerateObject().Select(p => p.Name).Take(2).ShouldBe(["id", "displayName"]);
        row.GetProperty("displayName").GetString().ShouldBe("Aufträge");
    }

    [Fact]
    public async Task DescribeDataset_Json_HasDisplayName()
    {
        var catalog = new StubMcpDatasetCatalog
        {
            OnDescribe = (_, _) => new McpDatasetDescription("sales.public.orders", "Aufträge", "sales", "public", "orders", "Orders", null, "INTERNAL", false, [], [])
        };

        var json = await Executor(catalog).ExecuteOperationAsync(Tool("describe_dataset"), """{"dataset":"sales.public.orders"}""", Session());

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("displayName").GetString().ShouldBe("Aufträge");
    }

    [Fact]
    public async Task Executor_HiddenOrMissingDataset_IsReportedAsNotFound()
    {
        var catalog = new StubMcpDatasetCatalog
        {
            OnDescribe = (_, _) => throw new TableNotFoundException(Orders)
        };

        var json = await Executor(catalog).ExecuteOperationAsync(Tool("describe_dataset"), """{"dataset":"sales.public.orders"}""", Session());

        json.ShouldContain("\"isError\":true");
        json.ShouldContain("NOT_FOUND");
        json.ShouldNotContain("does not exist");
    }

    [Fact]
    public async Task Executor_InvalidDatasetId_IsAnInvalidParamsError()
    {
        var catalog = new StubMcpDatasetCatalog
        {
            OnSample = (_, _, _) => throw new ArgumentException("The dataset id must have the form 'domain.schema.table'.")
        };

        var json = await Executor(catalog).ExecuteOperationAsync(Tool("sample_rows"), """{"dataset":"orders"}""", Session());

        json.ShouldContain("INVALID_PARAMS");
        json.ShouldNotContain("domain.schema.table"); // SEC-X1-01: the exception message is not echoed
    }

    [Fact]
    public async Task Executor_DeniedRead_IsForbidden()
    {
        var catalog = new StubMcpDatasetCatalog
        {
            OnSample = (_, _, _) => throw new GatewayForbiddenException()
        };

        var json = await Executor(catalog).ExecuteOperationAsync(Tool("sample_rows"), """{"dataset":"sales.public.orders"}""", Session());

        json.ShouldContain("FORBIDDEN");
    }

    private sealed class AnyTableMetadataRepo(bool requiresFourEyes) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult<TableMetadata?>(new TableMetadata
            {
                Identifier = table,
                Table = new Table(),
                Columns = requiresFourEyes ? [new TableColumn { ColumnName = "email" }] : [],
                ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor(requiresFourEyes ? new[] { "email" } : Array.Empty<string>())
            });

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TableMetadata>>([]);

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
            => Task.FromResult(metadata);

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class StubGraphQlCatalogMap(IReadOnlyList<TableIdentifier>? tables) : IGraphQlCatalogMap
    {
        public Task<GraphQlTableMapping?> GetTableAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult<GraphQlTableMapping?>(null);

        public Task<string?> GetRootFieldAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<TableIdentifier?> ResolveRootFieldAsync(string field, CancellationToken ct = default)
            => Task.FromResult<TableIdentifier?>(null);

        public Task<IReadOnlyList<TableIdentifier>?> ResolveDocumentTablesAsync(string documentText, CancellationToken ct = default)
            => Task.FromResult(tables);
    }
}
