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
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Every hint surface names only what the startup switches leave enabled.</summary>
public sealed class McpAccessHintTests
{
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");

    private static readonly string[] HintedTools =
    [
        "query_graphql", "query_sql", "query_dataset", "sample_rows", "describe_dataset", "find_relevant_tables",
        "search_catalog", "get_table_schema"
    ];

    private static GatewayOptions Opts(bool query = true, bool catalog = true, bool webSql = true, params string[] disabled) => new()
    {
        Mcp = new McpOptions { Enabled = true, Tools = new McpToolGroupOptions { Query = query, Catalog = catalog }, DisabledTools = [.. disabled] },
        WebSql = new WebSqlOptions { Enabled = webSql },
        DuckDbOlap = new DuckDbOlapOptions { Enabled = false }
    };

    private static McpDatasetCatalog Catalog(GatewayOptions o, IReadPathSwitches? switches = null)
    {
        var metadata = Substitute.For<ITableMetadataRepository>();
        var consents = Substitute.For<IConsentRepository>();
        var golden = Substitute.For<IGoldenQueryService>();
        var graphQl = Substitute.For<IGraphQlCatalogMap>();
        metadata.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>(
        [
            new TableMetadata
            {
                Identifier = Orders,
                Table = new Table { TableName = "orders", SchemaName = "public", Description = "Orders", Sensitivity = "INTERNAL" },
                PrimaryKeyColumns = ["id"],
                Columns = [new TableColumn { ColumnName = "id", DataType = "int", Description = "Key" }]
            }
        ]));
        golden.GetGoldenQueriesAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyList<GoldenQuery>>([]));
        return new McpDatasetCatalog(metadata, consents, Substitute.For<IGatewayExecutionService>(), golden, Options.Create(o), graphQl,
            visibilityService: Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(metadata.ServeSingleReadsFromCatalog(), consents, o),
            switches: switches ?? ReadPathSwitches.From(o));
    }

    private static ClaimsPrincipal Admin() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", "tenant-a"), new Claim(ClaimTypes.Role, "GovernanceAdmin")], "MCP"));

    private static GatewayOptions WithShared(GatewayOptions o) => new()
    {
        Mcp = o.Mcp, WebSql = o.WebSql, DuckDbOlap = o.DuckDbOlap, Catalog = new DataCatalogOptions { SharedTables = ["sales.public.orders"] }
    };

    /// <summary>Every hint surface as one text per surface, for the given options.</summary>
    private static async Task<Dictionary<string, string>> SurfacesAsync(GatewayOptions o, IReadPathSwitches? switches = null)
    {
        var sw = switches ?? ReadPathSwitches.From(o);
        var catalog = Catalog(WithShared(o), sw);
        var list = await catalog.ListDatasetsAsync(Admin(), null, null);
        var description = await catalog.DescribeDatasetAsync(Admin(), "sales.public.orders");
        return new Dictionary<string, string>
        {
            ["Guidance"] = list.Guidance ?? string.Empty,
            ["Endpoints"] = JsonSerializer.Serialize(list.Endpoints ?? []),
            ["DatasetAccess"] = JsonSerializer.Serialize(description.OtherAccess ?? []),
            ["ListDescription"] = McpDatasetTools.Definitions(sw).Single(t => t.Name == McpDatasetTools.ListDatasets).Description,
            ["Instructions"] = McpDatasetTools.BuildServerInstructions(sw)
        };
    }

    [Fact]
    public async Task Query_group_off_hides_query_tools_in_all_hints()
    {
        var all = await SurfacesAsync(Opts(query: false));
        foreach (var (surface, text) in all)
        {
            foreach (var tool in new[] { "query_graphql", "query_sql", "query_dataset", "sample_rows" })
            {
                text.ShouldNotContain(tool, customMessage: surface);
            }
        }
    }

    [Fact]
    public async Task DisabledTools_entry_never_recommended()
    {
        var all = await SurfacesAsync(Opts(disabled: ["query_graphql", "sample_rows"]));
        foreach (var (surface, text) in all)
        {
            text.ShouldNotContain("query_graphql", customMessage: surface);
            text.ShouldNotContain("sample_rows", customMessage: surface);
        }

        all["Instructions"].ShouldContain("query_sql");
    }

    [Fact]
    public async Task WebSql_off_drops_WebSql_and_Trino_endpoints()
    {
        var on = await SurfacesAsync(Opts(webSql: true));
        on["Endpoints"].ShouldContain("/api/v1/sql");
        var off = await SurfacesAsync(Opts(webSql: false));
        off["Endpoints"].ShouldNotContain("/api/v1/sql");
        off["Endpoints"].ShouldNotContain("/v1/statement");
    }

    [Fact]
    public async Task Nothing_enabled_drops_hint()
    {
        var all = await SurfacesAsync(Opts(query: false, catalog: false));
        all["Guidance"].ShouldBeEmpty();
        all["ListDescription"].ShouldNotContain("Start here");
        all["Instructions"].ShouldNotContain("1)");
    }

    [Fact]
    public async Task List_datasets_description_follows_switches()
    {
        var all = await SurfacesAsync(Opts());
        all["ListDescription"].ShouldContain("query_graphql");
        var off = await SurfacesAsync(Opts(disabled: "query_graphql"));
        off["ListDescription"].ShouldNotContain("query_graphql");
        off["ListDescription"].ShouldContain("describe_dataset");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_53_10_Disabled_tool_never_named_in_any_hint_surface()
    {
        foreach (var tool in HintedTools)
        {
            var all = await SurfacesAsync(Opts(disabled: tool));
            foreach (var (surface, text) in all)
            {
                text.ShouldNotContain(tool, customMessage: $"{tool} leaked in {surface}");
            }
        }
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_53_09_Hints_do_not_depend_on_request_or_principal()
    {
        var o = Opts();
        var catalog = Catalog(WithShared(o));
        var a = await catalog.ListDatasetsAsync(Admin(), null, null);
        var other = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-OTHER"), new Claim("tenant_id", "tenant-b"), new Claim(ClaimTypes.Role, "GovernanceAdmin")], "MCP"));
        var b = await catalog.ListDatasetsAsync(other, null, null);
        b.Guidance.ShouldBe(a.Guidance);
        JsonSerializer.Serialize(b.Endpoints).ShouldBe(JsonSerializer.Serialize(a.Endpoints));
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_53_12_Disabled_feature_not_mentioned_by_name_or_route()
    {
        var all = await SurfacesAsync(Opts(query: false, webSql: false));
        foreach (var text in all.Values)
        {
            text.ShouldNotContain("disabled", Case.Insensitive);
            text.ShouldNotContain("/api/v1/sql");
            text.ShouldNotContain("/v1/statement");
        }
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_53_13_Hint_naming_a_disabled_tool_cannot_enable_it()
    {
        var o = Opts(disabled: "query_graphql");
        var switches = ReadPathSwitches.From(o);
        (await SurfacesAsync(o, switches))["Guidance"].ShouldNotContain("query_graphql");
        switches.IsToolEnabled("query_graphql").ShouldBeFalse();

        // Execution is enforced independently of the hints: the registry has no such tool to find or call.
        var registry = new McpToolRegistry(Options.Create(o), switches: switches);
        registry.FindTool("query_graphql").ShouldBeNull();
        registry.GetAvailableTools().ShouldNotContain(t => t.Name == "query_graphql");
    }

    [Fact]
    public async Task Mcp_off_keeps_a_tool_free_GraphQL_sentence_for_REST_catalog()
    {
        var o = Opts();
        var off = new GatewayOptions { Mcp = new McpOptions { Enabled = false }, WebSql = o.WebSql, DuckDbOlap = o.DuckDbOlap };
        var all = await SurfacesAsync(off);
        all["Guidance"].ShouldContain("GraphQL endpoint");
        all["Guidance"].ShouldNotContain("query_graphql");
        all["Guidance"].ShouldNotContain("describe_dataset");
        all["Endpoints"].ShouldNotContain("query_graphql");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task SEC_53_10_Null_switches_fail_closed()
    {
        var o = Opts();
        var catalog = Catalog(WithShared(o), new NullSwitches());
        var list = await catalog.ListDatasetsAsync(Admin(), null, null);
        list.Guidance.ShouldBeNull();
    }

    private sealed class NullSwitches : IReadPathSwitches
    {
        public bool RestDataEndpointEnabled => false;
        public bool BulkStreamEnabled => false;
        public bool QueryAnalysisEnabled => false;
        public bool IsGroupEnabled(McpToolGroup group) => false;
        public bool IsToolEnabled(string toolName) => false;
        public string? DisabledReason(string toolName) => null;
        public bool IsResourceEnabled(string resourceUri) => false;
        public bool IsPromptEnabled(string promptName) => false;
        public void RecordDropped(string toolName) { }
    }
}
