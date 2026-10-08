namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class GoldenQuerySecurityTests
{
    private readonly IMcpToolRegistry _toolRegistry = Substitute.For<IMcpToolRegistry>();
    private readonly IGoldenQueryService _goldenService;
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IConsentRepository _consentRepo = Substitute.For<IConsentRepository>();

    private readonly TableMetadata _customersTable;
    private readonly TableMetadata _ordersTable;

    public GoldenQuerySecurityTests()
    {
        _toolRegistry.FindTool("get_golden_queries").Returns(new McpToolDefinition(
            Name: "get_golden_queries",
            Description: "Get golden queries",
            InputJsonSchema: "{}",
            TargetGraphQLOperation: string.Empty,
            TargetTable: null));

        _customersTable = new TableMetadata
        {
            Identifier = new TableIdentifier("finance", "dbo", "customers"),
            Table = new Table { SchemaName = "dbo", TableName = "customers" },
            Columns = [new TableColumn { ColumnName = "id", DataType = "varchar" }]
        };

        _ordersTable = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "orders"),
            Table = new Table { SchemaName = "dbo", TableName = "orders" },
            Columns = [new TableColumn { ColumnName = "id", DataType = "varchar" }]
        };

        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([_customersTable, _ordersTable]));

        _goldenService = new GoldenQueryService(Options.Create(new GatewayOptions()), NullLogger<GoldenQueryService>.Instance);
        _goldenService.RegisterGoldenQuery(new GoldenQuery(
            Id: "golden-customers",
            Domain: "finance",
            TableName: "customers",
            Title: "Get Active Customers",
            Description: "Returns active customers",
            QueryText: "query { finance { customers { id } } }"));

        _goldenService.RegisterGoldenQuery(new GoldenQuery(
            Id: "golden-orders",
            Domain: "sales",
            TableName: "orders",
            Title: "Get Recent Orders",
            Description: "Returns recent orders",
            QueryText: "query { sales { orders { id } } }"));
    }

    private AiDataGuardrailService CreateGuardrail(GatewayOptions? options = null)
    {
        return new AiDataGuardrailService(
            _toolRegistry,
            Options.Create(options ?? new GatewayOptions()),
            NullLogger<AiDataGuardrailService>.Instance,
            tableMetadataRepository: _metadataRepo,
            goldenQueryService: _goldenService,
            consentRepository: _consentRepo);
    }

    private static List<string> ExtractQueryIds(string contentJson)
    {
        using var doc = JsonDocument.Parse(contentJson);
        var ids = new List<string>();
        if (doc.RootElement.TryGetProperty("queries", out var qArr) && qArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var q in qArr.EnumerateArray())
            {
                if ((q.TryGetProperty("Id", out var idProp) || q.TryGetProperty("id", out idProp)) && idProp.ValueKind == JsonValueKind.String)
                {
                    ids.Add(idProp.GetString()!);
                }
            }
        }
        return ids;
    }

    [Fact]
    public async Task GetGoldenQueries_CallerWithoutConsent_ReturnsEmptyQueries()
    {
        // MCP-5: Non-admin caller without consent must not receive golden queries
        var guardrail = CreateGuardrail();
        var session = new McpSessionContext(
            SessionId: "sess-1",
            ServicePrincipalId: "sp-1",
            TenantId: "tenant-1",
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow,
            UserSid: "S-1-5-21-AGENT1",
            Roles: ["AiAgent"],
            GroupSids: []);

        _consentRepo.GetAllActiveConsentsForSubjectsAsync(
            Arg.Any<IEnumerable<Sid>>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<DateTimeOffset?>(),
            new TenantId("tenant-1"),
            Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Consent>());

        var request = new McpToolCallRequest(
            ToolName: "get_golden_queries",
            ArgumentsJson: """{ "domain": "finance", "tableName": "customers" }""",
            SessionId: session.SessionId);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        result.IsSuccess.ShouldBeTrue();
        var queryIds = ExtractQueryIds(result.ContentJson);
        queryIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetGoldenQueries_CallerWithConsentForTableA_CannotSeeTableB()
    {
        // MCP-5: Caller with consent for sales.orders must not see golden queries for finance.customers
        var guardrail = CreateGuardrail();
        var session = new McpSessionContext(
            SessionId: "sess-2",
            ServicePrincipalId: "sp-2",
            TenantId: "tenant-1",
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow,
            UserSid: "S-1-5-21-AGENT2",
            Roles: ["AiAgent"],
            GroupSids: []);

        var allowSales = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = new TenantId("tenant-1"),
            TableIdentifier = _ordersTable.Identifier,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-AGENT2"),
            RowFilters = [],
            ColumnRules = []
        };

        _consentRepo.GetAllActiveConsentsForSubjectsAsync(
            Arg.Any<IEnumerable<Sid>>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<DateTimeOffset?>(),
            new TenantId("tenant-1"),
            Arg.Any<CancellationToken>())
            .Returns(new[] { allowSales });

        var request = new McpToolCallRequest(
            ToolName: "get_golden_queries",
            ArgumentsJson: "{}",
            SessionId: session.SessionId);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        result.IsSuccess.ShouldBeTrue();
        var queryIds = ExtractQueryIds(result.ContentJson);
        queryIds.ShouldContain("golden-orders");
        queryIds.ShouldNotContain("golden-customers");
    }

    [Fact]
    public async Task GetGoldenQueries_CallerWithConsent_ReturnsGoldenQueries()
    {
        // MCP-5: Caller with active consent for finance.customers receives golden queries
        var guardrail = CreateGuardrail();
        var session = new McpSessionContext(
            SessionId: "sess-3",
            ServicePrincipalId: "sp-3",
            TenantId: "tenant-1",
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow,
            UserSid: "S-1-5-21-AGENT3",
            Roles: ["AiAgent"],
            GroupSids: []);

        var allowFinance = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = new TenantId("tenant-1"),
            TableIdentifier = _customersTable.Identifier,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-AGENT3"),
            RowFilters = [],
            ColumnRules = []
        };

        _consentRepo.GetAllActiveConsentsForSubjectsAsync(
            Arg.Any<IEnumerable<Sid>>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<DateTimeOffset?>(),
            new TenantId("tenant-1"),
            Arg.Any<CancellationToken>())
            .Returns(new[] { allowFinance });

        var request = new McpToolCallRequest(
            ToolName: "get_golden_queries",
            ArgumentsJson: """{ "domain": "finance", "tableName": "customers" }""",
            SessionId: session.SessionId);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        result.IsSuccess.ShouldBeTrue();
        var queryIds = ExtractQueryIds(result.ContentJson);
        queryIds.ShouldContain("golden-customers");
    }

    [Fact]
    public async Task GetGoldenQueries_AdminCaller_BypassesConsentCheck()
    {
        // MCP-5: ClusterAdmin or GovernanceAdmin receives all queries without explicit consent
        var guardrail = CreateGuardrail();
        var session = new McpSessionContext(
            SessionId: "sess-admin",
            ServicePrincipalId: "sp-admin",
            TenantId: "tenant-1",
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow,
            UserSid: "S-1-5-21-ADMIN",
            Roles: ["ClusterAdmin"],
            GroupSids: []);

        var request = new McpToolCallRequest(
            ToolName: "get_golden_queries",
            ArgumentsJson: "{}",
            SessionId: session.SessionId);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        result.IsSuccess.ShouldBeTrue();
        var queryIds = ExtractQueryIds(result.ContentJson);
        queryIds.ShouldContain("golden-customers");
        queryIds.ShouldContain("golden-orders");
    }

    [Fact]
    public async Task GetGoldenQueries_AnonymousCaller_ReturnsEmptyQueries()
    {
        // MCP-5: Anonymous MCP client receives empty queries
        var guardrail = CreateGuardrail();
        var session = new McpSessionContext(
            SessionId: "sess-anon",
            ServicePrincipalId: "sp-anon",
            TenantId: "tenant-1",
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow,
            UserSid: "ANONYMOUS_MCP_CLIENT",
            Roles: [],
            GroupSids: []);

        var request = new McpToolCallRequest(
            ToolName: "get_golden_queries",
            ArgumentsJson: "{}",
            SessionId: session.SessionId);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(request, session);

        result.IsSuccess.ShouldBeTrue();
        var queryIds = ExtractQueryIds(result.ContentJson);
        queryIds.ShouldBeEmpty();
    }
}
