namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
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

/// <summary>
/// MCP-13 / MCP-14: semantic resources honour ReBAC through the catalog visibility service, are listed as paged descriptors and are
/// rendered per read; table cells of native resources cannot break out of the cell.
/// </summary>
[Trait("Category", "Security")]
public sealed class McpSemanticResourceSecurityTests
{
    private const string Tenant = "tenant-a";
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");
    private static readonly TableIdentifier Payroll = new("hr", "public", "payroll");

    private static TableMetadata Table(TableIdentifier id) => new()
    {
        Identifier = id,
        Table = new Table { TableName = id.TableName, SchemaName = id.Schema, SourceName = id.Domain, SourceType = "PostgreSQL" },
        Columns = [new TableColumn { ColumnName = "id", DataType = "int", Description = "Key" }]
    };

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-MCP-USER"), new Claim("tenant_id", Tenant)], "MCP"));

    private static IConsentRepository AllowAll(IEnumerable<TableIdentifier> tables)
    {
        var consents = tables.Select(t => new Consent { TableIdentifier = t, Effect = ConsentEffect.Allow, TenantId = new TenantId(Tenant) }).ToList();
        var repo = Substitute.For<IConsentRepository>();
        repo.GetAllActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<IEnumerable<string>?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(consents));
        return repo;
    }

    private static ICatalogVisibilityService Rebac(params TableIdentifier[] visible)
    {
        var service = Substitute.For<ICatalogVisibilityService>();
        service.FilterVisibleAsync(Arg.Any<CatalogCaller>(), Arg.Any<IReadOnlyCollection<TableIdentifier>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ValueTask.FromResult<IReadOnlySet<TableIdentifier>>(
                ci.Arg<IReadOnlyCollection<TableIdentifier>>().Where(visible.Contains).ToHashSet()));
        return service;
    }

    private static SemanticMcpCompiler Compiler(
        IReadOnlyList<TableMetadata> tables,
        ICatalogVisibilityService? visibility,
        IGoldenQueryService? golden = null)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(tables));
        return new SemanticMcpCompiler(
            repo, NullLogger<SemanticMcpCompiler>.Instance, golden, AllowAll(tables.Select(t => t.Identifier)),
            catalogVisibility: visibility);
    }

    [Fact]
    public async Task SemanticResources_RebacDeniedTable_NotListedNotReadable()
    {
        // MCP-14: consent allows both tables, ReBAC allows only orders.
        var compiler = Compiler([Table(Orders), Table(Payroll)], Rebac(Orders));

        var page = await compiler.ListSemanticResourceUrisAsync(User());
        page.Items.ShouldContain(r => r.Uri == "glossary://sales/orders");
        page.Items.ShouldNotContain(r => r.Uri.Contains("payroll"));

        (await compiler.ReadSemanticResourceAsync(User(), "glossary://sales/orders")).ShouldNotBeNull();
        (await compiler.ReadSemanticResourceAsync(User(), "glossary://hr/payroll")).ShouldBeNull();
        (await compiler.ReadSemanticResourceAsync(User(), "glossary://hr/missing")).ShouldBeNull();
    }

    [Fact]
    public async Task GetGoldenQueries_RebacDenied_Empty()
    {
        // MCP-14: the golden-query tool uses the same visibility (consent AND ReBAC).
        var registry = Substitute.For<IMcpToolRegistry>();
        registry.FindTool("get_golden_queries").Returns(new McpToolDefinition("get_golden_queries", "Golden", "{}", string.Empty, null));
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([Table(Orders)]));
        var golden = new GoldenQueryService(Options.Create(new GatewayOptions()), NullLogger<GoldenQueryService>.Instance);
        golden.RegisterGoldenQuery(new GoldenQuery("g1", "sales", "orders", "Orders", "d", "query { sales { orders { id } } }"));
        var guardrail = new AiDataGuardrailService(
            registry,
            Options.Create(new GatewayOptions()),
            NullLogger<AiDataGuardrailService>.Instance,
            global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            tableMetadataRepository: repo,
            goldenQueryService: golden,
            consentRepository: AllowAll([Orders]),
            catalogVisibility: Rebac());
        var session = new McpSessionContext("s1", "sp", Tenant, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-5-21-MCP-USER", Roles: ["AiAgent"], GroupSids: []);

        var result = await guardrail.ExecuteToolWithGuardrailAsync(new McpToolCallRequest("get_golden_queries", "{}", session.SessionId), session);

        result.IsSuccess.ShouldBeTrue();
        result.ContentJson.ShouldNotContain("g1");
    }

    [Fact]
    public async Task FourEyesPreCheck_UsesRebacVisibility()
    {
        // MCP-14: the visibility overload used by the four-eyes pre-check removes a ReBAC-denied table.
        var tables = new List<TableMetadata> { Table(Orders) };

        var denied = await McpCatalogVisibility.VisibleTablesAsync(tables, User(), AllowAll([Orders]), null, Rebac(), CancellationToken.None);
        var allowed = await McpCatalogVisibility.VisibleTablesAsync(tables, User(), AllowAll([Orders]), null, Rebac(Orders), CancellationToken.None);

        denied.ShouldBeEmpty();
        allowed.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task ResourcesList_Paginates100_WithCursor()
    {
        // 60 tables with one documented column each: 120 descriptors (glossary + column docs).
        var tables = Enumerable.Range(0, 60).Select(i => Table(new TableIdentifier("d", "s", $"t{i:D3}"))).ToList();
        var compiler = Compiler(tables, Rebac(tables.Select(t => t.Identifier).ToArray()));

        var first = await compiler.ListSemanticResourceUrisAsync(User());
        first.Items.Count.ShouldBe(100);
        first.NextCursor.ShouldNotBeNull();
        first.Items.ShouldAllBe(r => r.Text == string.Empty);

        var second = await compiler.ListSemanticResourceUrisAsync(User(), first.NextCursor);
        second.Items.Count.ShouldBe(20);
        second.NextCursor.ShouldBeNull();

        (await compiler.ListSemanticResourceUrisAsync(User(), "not-a-cursor")).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResourceRead_RendersSingleTableOnly()
    {
        var golden = Substitute.For<IGoldenQueryService>();
        golden.GetGoldenQueriesAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<GoldenQuery>>([new GoldenQuery("g1", "sales", "orders", "Orders", "d", "query { x }")]));
        var compiler = Compiler([Table(Orders), Table(Payroll)], Rebac(Orders, Payroll), golden);

        var item = await compiler.ReadSemanticResourceAsync(User(), "examples://sales/orders");

        item.ShouldNotBeNull();
        await golden.Received(1).GetGoldenQueriesAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await golden.Received(1).GetGoldenQueriesAsync("sales", "orders", Arg.Any<CancellationToken>());

        await compiler.ReadSemanticResourceAsync(User(), "glossary://hr/payroll");
        await golden.Received(1).GetGoldenQueriesAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("a|b", "a\\|b")]
    [InlineData("line1\r\nline2", "line1  line2")]
    [InlineData("tick`s", "tick\\`s")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData(null, "-")]
    public void EscapeCell_EscapesCellBreakouts(string? input, string expected)
    {
        McpMarkdown.EscapeCell(input).ShouldBe(expected);
    }

    [Fact]
    public async Task CatalogSummary_PipeAndNewlineInDescription_Escaped()
    {
        // MCP-13: an externally authored description cannot end the cell or start a new table row.
        var discovery = Substitute.For<ICatalogDiscoveryService>();
        discovery.ListDatasetsAsync(Arg.Any<RequestContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CatalogDatasetSummary>>(
                [new CatalogDatasetSummary("sales.public.orders", "sales", "public", "orders", "Sql", "Internal", "x | injected\n| fake | row |", true)]));
        var provider = new McpResourceProvider(discovery, Substitute.For<IApiDispatcherService>());
        var session = new McpSessionContext("s1", "agent", Tenant, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-USER");

        var content = await provider.ReadAsync("autheris://catalog/summary", session);

        content.Text.ShouldContain("x \\| injected \\| fake \\| row \\|");
        content.Text.Split('\n').Count(l => l.StartsWith("| `sales.public.orders`", StringComparison.Ordinal)).ShouldBe(1);
        content.Text.ShouldNotContain("\n| fake");
    }
}
