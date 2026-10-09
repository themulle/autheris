namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Mcp;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class McpHybridToolsTests
{
    private readonly IGovernedSqlExecutionService _sqlExecutionService = Substitute.For<IGovernedSqlExecutionService>();
    private readonly IGovernedDataQueryService _dataQueryService = Substitute.For<IGovernedDataQueryService>();
    private readonly ICatalogDiscoveryService _catalogDiscoveryService = Substitute.For<ICatalogDiscoveryService>();
    private readonly IApiDispatcherService _apiDispatcherService = Substitute.For<IApiDispatcherService>();
    private readonly ILineageGraphStore _lineageGraphStore = Substitute.For<ILineageGraphStore>();
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();

    private McpSessionContext CreateSessionContext(string userSid = "user-1", string tenant = "tenant-a") =>
        new(
            SessionId: "session-123",
            ServicePrincipalId: userSid,
            TenantId: tenant,
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow,
            UserSid: userSid,
            Roles: ["DataAnalyst"],
            GroupSids: ["group-sales"],
            ClientIp: "10.0.0.1",
            IsReadOnly: false,
            AdditionalClaims: new Dictionary<string, string>());

    [Fact]
    public async Task ToolExecution_QuerySql_ExecutesUnderCallerContextWithMasking()
    {
        var handler = new McpToolExecutionHandler(
            _sqlExecutionService,
            _dataQueryService,
            _catalogDiscoveryService,
            _apiDispatcherService,
            _lineageGraphStore,
            _metadataRepo);

        var tool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.QuerySql);
        var session = CreateSessionContext("user:alice", "tenant-1");
        var query = "SELECT id, email FROM sales.public.customers";

        _sqlExecutionService.ExecuteQueryBufferedAsync(
            Arg.Is<GovernedSqlQueryRequest>(r => r.Sql == query),
            Arg.Is<ClaimsPrincipal>(p => p.Claims.Any(c => c.Value == "user:alice")),
            Arg.Is<TenantId>(t => t.Value == "tenant-1"),
            Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult(
                OriginalSql: query,
                RewrittenSql: "SELECT id, mask(email) FROM sales.public.customers",
                Columns: ["id", "email"],
                Rows: [new Dictionary<string, object?> { ["id"] = 1, ["email"] = "a***@example.com" }],
                RowCount: 1,
                ElapsedMilliseconds: 15));

        var resultJson = await handler.ExecuteToolAsync(tool, JsonSerializer.Serialize(new { query }), session);

        resultJson.ShouldNotBeNullOrWhiteSpace();
        resultJson.ShouldContain("a***@example.com");
        resultJson.ShouldContain("\"rowCount\":1");
    }

    [Fact]
    public async Task ToolExecution_QueryDataset_DelegatesToGovernedDataQueryService()
    {
        var handler = new McpToolExecutionHandler(
            _sqlExecutionService,
            _dataQueryService,
            _catalogDiscoveryService,
            _apiDispatcherService,
            _lineageGraphStore,
            _metadataRepo);

        var tool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.QueryDataset);
        var session = CreateSessionContext("user:bob", "tenant-1");

        var envelope = new DatasetQueryEnvelope(
            Dataset: "sales.public.orders",
            Count: 1,
            Offset: 0,
            Limit: 10,
            HasMore: false,
            Columns: [new DatasetColumnInfo("id", "int", false), new DatasetColumnInfo("amount", "decimal", false)],
            Data: [new Dictionary<string, object?> { ["id"] = 100, ["amount"] = 99.5m }]);

        _dataQueryService.ExecuteQueryAsync(
            Arg.Is<DatasetQueryRequest>(r => r.Table.ToString() == "sales.public.orders" && r.Limit == 10),
            Arg.Any<RequestContext>(),
            Arg.Any<CancellationToken>())
            .Returns(envelope);

        var args = JsonSerializer.Serialize(new
        {
            dataset = "sales.public.orders",
            select = new[] { "id", "amount" },
            limit = 10
        });

        var resultJson = await handler.ExecuteToolAsync(tool, args, session);

        resultJson.ShouldContain("sales.public.orders");
        resultJson.ShouldContain("99.5");
        await _dataQueryService.Received(1).ExecuteQueryAsync(
            Arg.Is<DatasetQueryRequest>(r => r.Table.Domain == "sales" && r.Table.TableName == "orders"),
            Arg.Any<RequestContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ToolExecution_SearchCatalog_DelegatesToCatalogDiscoveryService()
    {
        var handler = new McpToolExecutionHandler(
            _sqlExecutionService,
            _dataQueryService,
            _catalogDiscoveryService,
            _apiDispatcherService,
            _lineageGraphStore,
            _metadataRepo);

        var tool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.SearchCatalog);
        var session = CreateSessionContext("user:carol", "tenant-1");

        _catalogDiscoveryService.SearchCatalogAsync("orders", "sales", Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns([new CatalogDatasetSummary("sales.public.orders", "sales", "public", "orders", "Sql", "Internal", "Orders table", true)]);

        var args = JsonSerializer.Serialize(new { query = "orders", domain = "sales" });
        var resultJson = await handler.ExecuteToolAsync(tool, args, session);

        resultJson.ShouldContain("sales.public.orders");
        await _catalogDiscoveryService.Received(1).SearchCatalogAsync("orders", "sales", Arg.Any<RequestContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ToolExecution_GetMyPermissions_ReturnsRebacAndColumnPermissions()
    {
        var handler = new McpToolExecutionHandler(
            _sqlExecutionService,
            _dataQueryService,
            _catalogDiscoveryService,
            _apiDispatcherService,
            _lineageGraphStore,
            _metadataRepo);

        var tool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.GetMyPermissions);
        var session = CreateSessionContext("user:dave", "tenant-1");

        _catalogDiscoveryService.GetDatasetDetailAsync(new TableIdentifier("sales", "public", "orders"), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new CatalogDatasetDetail(
                DatasetId: "sales.public.orders",
                Domain: "sales",
                Schema: "public",
                Table: "orders",
                Columns: [
                    new CatalogColumnDetail("id", "int", "Internal", "clear", true, false),
                    new CatalogColumnDetail("customer_email", "varchar", "Confidential", "mask", false, true)
                ],
                PrimaryKeys: ["id"],
                Sensitivity: "Internal",
                IsActive: true));

        var args = JsonSerializer.Serialize(new { dataset = "sales.public.orders" });
        var resultJson = await handler.ExecuteToolAsync(tool, args, session);

        resultJson.ShouldContain("sales.public.orders");
        resultJson.ShouldContain("customer_email");
        resultJson.ShouldContain("mask");
    }

    [Fact]
    public async Task ToolExecution_ListDatasources_ListsDataSources()
    {
        var handler = new McpToolExecutionHandler(
            _sqlExecutionService,
            _dataQueryService,
            _catalogDiscoveryService,
            _apiDispatcherService,
            _lineageGraphStore,
            _metadataRepo);

        var tool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.ListDatasources);
        var session = CreateSessionContext("user:eve", "tenant-1");

        _catalogDiscoveryService.ListDatasourcesAsync(Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns([new CatalogDatasourceSummary("ds-1", "Warehouse", "sales", "PostgreSql", null, true, "active", DateTimeOffset.UtcNow)]);

        var args = JsonSerializer.Serialize(new { status = "active" });
        var resultJson = await handler.ExecuteToolAsync(tool, args, session);

        resultJson.ShouldContain("Warehouse");
        resultJson.ShouldContain("PostgreSql");
        resultJson.ShouldNotContain("secret");
    }

    [Fact]
    public async Task ToolExecution_GetDataLineage_ReturnsLineageInformation()
    {
        var handler = new McpToolExecutionHandler(
            _sqlExecutionService,
            _dataQueryService,
            _catalogDiscoveryService,
            _apiDispatcherService,
            _lineageGraphStore,
            _metadataRepo);

        var tool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.GetDataLineage);
        var session = CreateSessionContext("user:frank", "tenant-1");

        _lineageGraphStore.GetNode("sales.public.orders")
            .Returns(new LineageNode("sales.public.orders", "Orders Dataset", LineageNodeType.Table, ["analytics.sales_mrr"], "OrderTeam", "team@orders.com"));

        var args = JsonSerializer.Serialize(new { dataset = "sales.public.orders" });
        var resultJson = await handler.ExecuteToolAsync(tool, args, session);

        resultJson.ShouldContain("sales.public.orders");
        resultJson.ShouldContain("analytics.sales_mrr");
    }

    [Fact]
    public async Task ToolExecution_DescribeApiAndInvokeApi_InspectsAndDispatches()
    {
        var handler = new McpToolExecutionHandler(
            _sqlExecutionService,
            _dataQueryService,
            _catalogDiscoveryService,
            _apiDispatcherService,
            _lineageGraphStore,
            _metadataRepo);

        var descTool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.DescribeApi);
        var invokeTool = McpDatasetTools.Definitions().First(t => t.Name == McpDatasetTools.InvokeApi);
        var session = CreateSessionContext("user:grace", "tenant-1");

        _apiDispatcherService.DescribeApiAsync("/api/v1/data/{domain}/{table}", "GET", Arg.Any<CancellationToken>())
            .Returns("{\"endpoint\": \"/api/v1/data/{domain}/{table}\", \"method\": \"GET\"}");

        _apiDispatcherService.InvokeApiAsync("/api/v1/catalog/datasets", "GET", Arg.Any<IReadOnlyDictionary<string, object?>>(), Arg.Any<object?>(), Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns("{\"datasets\": [\"sales.public.orders\"]}");

        var descArgs = JsonSerializer.Serialize(new { endpoint = "/api/v1/data/{domain}/{table}", method = "GET" });
        var descResult = await handler.ExecuteToolAsync(descTool, descArgs, session);
        descResult.ShouldContain("/api/v1/data/{domain}/{table}");

        var invokeArgs = JsonSerializer.Serialize(new { endpoint = "/api/v1/catalog/datasets", method = "GET" });
        var invokeResult = await handler.ExecuteToolAsync(invokeTool, invokeArgs, session);
        invokeResult.ShouldContain("sales.public.orders");
    }

    [Fact]
    public async Task McpResources_CatalogSummaryAndDatasources_ReturnsExpectedContent()
    {
        var services = new ServiceCollection();
        var options = new GatewayOptions { Mcp = new McpOptions { Enabled = true } };
        services.AddSingleton(Options.Create(options));

        _catalogDiscoveryService.ListDatasetsAsync(Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns([new CatalogDatasetSummary("sales.public.orders", "sales", "public", "orders", "Sql", "Internal", "Orders dataset", true)]);

        _catalogDiscoveryService.ListDatasourcesAsync(Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns([new CatalogDatasourceSummary("ds-1", "SalesDW", "sales", "SqlServer", null, true, "healthy", DateTimeOffset.UtcNow)]);

        _apiDispatcherService.GetOpenApiJsonAsync(Arg.Any<CancellationToken>())
            .Returns("{\"openapi\": \"3.1.0\"}");
        _apiDispatcherService.GetEndpointsDocumentationAsync(Arg.Any<CancellationToken>())
            .Returns("# API Endpoints\n- GET /api/v1/data/{domain}/{table}");
        _apiDispatcherService.GetMcpToolsDocumentationAsync(Arg.Any<CancellationToken>())
            .Returns("# MCP Tools\n- query_sql\n- query_dataset");

        services.AddSingleton(_catalogDiscoveryService);
        services.AddSingleton(_apiDispatcherService);

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "user-mcp"),
            new Claim("tid", "tenant-test")
        ], "TestAuth"));
        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        httpContextAccessor.HttpContext.Returns(httpContext);
        services.AddSingleton(httpContextAccessor);

        var sp = services.BuildServiceProvider();

        // 1. autheris://catalog/summary
        var summaryResult = await GatewayMcpServer.ReadNativeResourceAsync("autheris://catalog/summary", sp, CancellationToken.None);
        summaryResult.Contents.ShouldNotBeEmpty();
        var summaryText = ((TextResourceContents)summaryResult.Contents[0]).Text;
        summaryText.ShouldContain("sales.public.orders");

        // 2. autheris://catalog/datasources
        var dsResult = await GatewayMcpServer.ReadNativeResourceAsync("autheris://catalog/datasources", sp, CancellationToken.None);
        dsResult.Contents.ShouldNotBeEmpty();
        var dsText = ((TextResourceContents)dsResult.Contents[0]).Text;
        dsText.ShouldContain("SalesDW");

        // 3. autheris://governance/my-access
        var accessResult = await GatewayMcpServer.ReadNativeResourceAsync("autheris://governance/my-access", sp, CancellationToken.None);
        accessResult.Contents.ShouldNotBeEmpty();
        var accessText = ((TextResourceContents)accessResult.Contents[0]).Text;
        accessText.ShouldContain("user-mcp");

        // 4. autheris://api/openapi.json
        var openApiResult = await GatewayMcpServer.ReadNativeResourceAsync("autheris://api/openapi.json", sp, CancellationToken.None);
        ((TextResourceContents)openApiResult.Contents[0]).Text.ShouldContain("3.1.0");

        // 5. autheris://api/docs/endpoints
        var epDocResult = await GatewayMcpServer.ReadNativeResourceAsync("autheris://api/docs/endpoints", sp, CancellationToken.None);
        ((TextResourceContents)epDocResult.Contents[0]).Text.ShouldContain("/api/v1/data");

        // 6. autheris://api/docs/mcp-tools
        var toolsDocResult = await GatewayMcpServer.ReadNativeResourceAsync("autheris://api/docs/mcp-tools", sp, CancellationToken.None);
        ((TextResourceContents)toolsDocResult.Contents[0]).Text.ShouldContain("query_sql");
    }

    [Fact]
    public void McpPrompts_ExploreDatasetAndAuditAccess_ReturnPromptsAndMessages()
    {
        var prompts = GatewayMcpServer.GetAvailablePrompts();
        prompts.ShouldContain(p => p.Name == "explore_dataset");
        prompts.ShouldContain(p => p.Name == "audit_access_compliance");

        var explorePrompt = GatewayMcpServer.BuildPrompt("explore_dataset", new Dictionary<string, string?> { ["dataset"] = "sales.public.orders" });
        explorePrompt.ShouldNotBeNull();
        explorePrompt.Messages.ShouldNotBeEmpty();
        var exploreMsg = explorePrompt.Messages[0].Content as TextContentBlock;
        exploreMsg!.Text.ShouldContain("sales.public.orders");

        var auditPrompt = GatewayMcpServer.BuildPrompt("audit_access_compliance", new Dictionary<string, string?> { ["dataset"] = "sales.public.orders", ["principal"] = "user:bob" });
        auditPrompt.ShouldNotBeNull();
        auditPrompt.Messages.ShouldNotBeEmpty();
        var auditMsg = auditPrompt.Messages[0].Content as TextContentBlock;
        auditMsg!.Text.ShouldContain("user:bob");
    }
}
