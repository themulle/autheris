namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
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
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, tableMetadataRepository: _metadataRepo,
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
    [Trait("Category", "Security")]
    public async Task MCP_15_GetGoldenQueries_WithoutConsentRepository_FailsClosed()
    {
        var guardrail = new AiDataGuardrailService(
            _toolRegistry,
            Options.Create(new GatewayOptions()),
            NullLogger<AiDataGuardrailService>.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, tableMetadataRepository: _metadataRepo,
            goldenQueryService: _goldenService);
        var session = new McpSessionContext(
            SessionId: "sess-norepo",
            ServicePrincipalId: "sp-1",
            TenantId: "tenant-1",
            CreatedAt: DateTimeOffset.UtcNow,
            LastActiveAt: DateTimeOffset.UtcNow,
            UserSid: "S-1-5-21-AGENT1",
            Roles: ["AiAgent"],
            GroupSids: []);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(
            new McpToolCallRequest(ToolName: "get_golden_queries", ArgumentsJson: "{}", SessionId: session.SessionId), session);

        result.IsSuccess.ShouldBeTrue();
        ExtractQueryIds(result.ContentJson).ShouldBeEmpty();
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

    // ---- MCP-15: the per-request cap applies after the visibility filter ----

    private static GoldenQueryService CappedService(int cap, string domain, string table, int hidden, int visible)
    {
        var demo = Substitute.For<IDemoDataSwitch>();
        demo.Enabled.Returns(false);
        var service = new GoldenQueryService(
            Options.Create(new GatewayOptions { GoldenQueries = new GoldenQueryOptions { MaxResultsPerRequest = cap } }),
            NullLogger<GoldenQueryService>.Instance,
            demo);
        for (var i = 0; i < hidden; i++)
        {
            service.RegisterGoldenQuery(new GoldenQuery($"hidden-{i}", "hr", "payroll", "t", "d", "query { hr { payroll { id } } }"));
        }

        for (var i = 0; i < visible; i++)
        {
            service.RegisterGoldenQuery(new GoldenQuery($"visible-{i}", domain, table, "t", "d", "query { x { y { id } } }"));
        }

        return service;
    }

    private AiDataGuardrailService GuardrailWith(IGoldenQueryService golden, bool withConsentRepo = true) => new(
        _toolRegistry,
        Options.Create(new GatewayOptions()),
        NullLogger<AiDataGuardrailService>.Instance,
        global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
        tableMetadataRepository: _metadataRepo,
        goldenQueryService: golden,
        consentRepository: withConsentRepo ? _consentRepo : null);

    private static McpSessionContext Session(string sid, string role) => new(
        SessionId: "sess-" + sid,
        ServicePrincipalId: "sp-" + sid,
        TenantId: "tenant-1",
        CreatedAt: DateTimeOffset.UtcNow,
        LastActiveAt: DateTimeOffset.UtcNow,
        UserSid: "S-1-5-21-" + sid,
        Roles: [role],
        GroupSids: []);

    private void AllowFinanceFor(string sid) =>
        _consentRepo.GetAllActiveConsentsForSubjectsAsync(
            Arg.Any<IEnumerable<Sid>>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<DateTimeOffset?>(),
            new TenantId("tenant-1"),
            Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Consent
                {
                    Id = Guid.NewGuid(),
                    TenantId = new TenantId("tenant-1"),
                    TableIdentifier = _customersTable.Identifier,
                    Effect = ConsentEffect.Allow,
                    GranteeType = GranteeType.User,
                    GranteeSid = new Sid("S-1-5-21-" + sid),
                    RowFilters = [],
                    ColumnRules = []
                }
            });

    private static ValueTask<McpToolCallResult> CallGolden(AiDataGuardrailService guardrail, McpSessionContext session) =>
        guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest("get_golden_queries", "{}", session.SessionId), session);

    [Fact]
    [Trait("Category", "Security")]
    public async Task Mcp15_MoreGoldensThanCap_VisibleOnesStillReturned()
    {
        var session = Session("MCP15A", "AiAgent");
        AllowFinanceFor("MCP15A");
        var guardrail = GuardrailWith(CappedService(2, "finance", "customers", hidden: 10, visible: 1));

        var result = await CallGolden(guardrail, session);

        ExtractQueryIds(result.ContentJson).ShouldBe(["visible-0"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Mcp15_CapStillLimitsFilteredResult()
    {
        var session = Session("MCP15B", "AiAgent");
        AllowFinanceFor("MCP15B");
        var guardrail = GuardrailWith(CappedService(2, "finance", "customers", hidden: 3, visible: 6));

        var ids = ExtractQueryIds((await CallGolden(guardrail, session)).ContentJson);

        ids.Count.ShouldBe(2);
        ids.ShouldAllBe(id => id.StartsWith("visible-", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Mcp15_AdminPath_StillCapped()
    {
        var guardrail = GuardrailWith(CappedService(2, "finance", "customers", hidden: 3, visible: 3));

        var ids = ExtractQueryIds((await CallGolden(guardrail, Session("MCP15C", "ClusterAdmin"))).ContentJson);

        ids.Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Mcp15_NoConsentRepo_FailsClosedWithoutServiceCall()
    {
        var golden = Substitute.For<IGoldenQueryService>();
        var guardrail = GuardrailWith(golden, withConsentRepo: false);

        var result = await CallGolden(guardrail, Session("MCP15D", "AiAgent"));

        ExtractQueryIds(result.ContentJson).ShouldBeEmpty();
        golden.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Mcp15_ConsentRepoThrows_ReturnsEmpty()
    {
        var golden = Substitute.For<IGoldenQueryService>();
        _consentRepo.GetAllActiveConsentsForSubjectsAsync(
            Arg.Any<IEnumerable<Sid>>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<TenantId>(),
            Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<Consent>>(_ => throw new InvalidOperationException("repo down"));
        var guardrail = GuardrailWith(golden);

        var result = await CallGolden(guardrail, Session("MCP15E", "AiAgent"));

        ExtractQueryIds(result.ContentJson).ShouldBeEmpty();
        golden.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Mcp15_PredicateThrows_EntryExcluded()
    {
        var service = CappedService(10, "finance", "customers", hidden: 0, visible: 3);

        var result = await service.GetGoldenQueriesAsync(null, null, q => q.Id == "visible-1" ? throw new InvalidOperationException("boom") : true, CancellationToken.None);

        result.Select(q => q.Id).OrderBy(x => x).ShouldBe(["visible-0", "visible-2"]);
    }
}
