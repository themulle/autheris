namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// PLAN-SEC-115 WP-3 (A-5, C-115-6, C-115-10): <c>invoke_api</c> composes the REST data, catalog, statement and access paths
/// in-process, so unmapping the REST route alone would not close it. Every branch checks the group of what it reaches as its first
/// statement; a refused branch answers exactly like an unknown endpoint and never calls a service.
/// </summary>
public sealed class ApiDispatcherSwitchTests
{
    private const string Never = "/api/v1/zzz-never-existed";

    private sealed record Rig(
        ApiDispatcherService Dispatcher,
        IGovernedDataQueryService Data,
        ICatalogDiscoveryService Catalog,
        IGovernedSqlExecutionService Sql);

    private static GatewayOptions Opts(bool rest = true, bool catalog = true, bool query = true, bool api = true, bool access = true, string[]? disabled = null) =>
        new()
        {
            RestApi = new RestApiOptions { DataEndpoint = new RestDataEndpointOptions { Enabled = rest } },
            Mcp = new McpOptions { Tools = new McpToolGroupOptions { Catalog = catalog, Query = query, Api = api, Access = access }, DisabledTools = disabled?.ToList() }
        };

    private static Rig Build(GatewayOptions? o)
    {
        var data = Substitute.For<IGovernedDataQueryService>();
        var catalog = Substitute.For<ICatalogDiscoveryService>();
        var sql = Substitute.For<IGovernedSqlExecutionService>();
        var switches = o is null ? null : Autheris.Application.Mcp.Services.ReadPathSwitches.From(o);
        var dispatcher = new ApiDispatcherService(data, catalog, sql, switches: switches);
        return new Rig(dispatcher, data, catalog, sql);
    }

    private static string Echo(string text, string endpoint) => text.Replace(endpoint, "<E>", StringComparison.Ordinal);

    // ---- defaults ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Defaults_reach_every_branch_with_and_without_switches()
    {
        foreach (var o in new GatewayOptions?[] { null, Opts() })
        {
            var rig = Build(o);

            await rig.Dispatcher.InvokeApiAsync("/api/v1/data/sales/public/orders", "GET");
            await rig.Dispatcher.InvokeApiAsync("/api/v1/catalog/datasets", "GET");
            await rig.Dispatcher.InvokeApiAsync("/api/v1/catalog/search", "GET");
            await rig.Dispatcher.InvokeApiAsync("/api/v1/statement", "POST", new Dictionary<string, object?> { ["sql"] = "select 1" });

            rig.Data.ReceivedCalls().ShouldNotBeEmpty();
            rig.Catalog.ReceivedCalls().ShouldNotBeEmpty();
            rig.Sql.ReceivedCalls().ShouldNotBeEmpty();
            (await rig.Dispatcher.InvokeApiAsync("/api/v1/governance/me/access", "GET")).ShouldContain("subjectId");
        }
    }

    // ---- ST-115-12: refused branches ---------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> Refused()
    {
        string[] methods = ["GET", "POST", "PUT", "DELETE"];
        string[] dataEndpoints =
        [
            "/api/v1/data/d.s.t", "/api/v1/data/d/s/t/change-token", "/API/V1/Data/x", " /api/v1/data/x ", "/api/v1/data/d.s.t?x=1", "/api/v1/database"
        ];

        foreach (var m in methods)
        {
            foreach (var e in dataEndpoints)
            {
                yield return ["rest", e, m];
                yield return ["query", e, m];
            }

            foreach (var e in new[] { "/api/v1/catalog/datasets", "/api/v1/catalog/datasets/a.b.c", "/api/v1/catalog/search?q=x", "/api/v1/catalog/datasources", "/API/V1/CATALOG/DATASETS" })
            {
                yield return ["catalog", e, m];
            }

            yield return ["access", "/api/v1/governance/me/access", m];
            yield return ["access", " /API/v1/governance/me/ACCESS ", m];
        }

        yield return ["query", "/api/v1/statement", "POST"];
        yield return ["query", "/API/V1/STATEMENT", "post"];
    }

    private static GatewayOptions Closed(string which) => which switch
    {
        "rest" => Opts(rest: false),
        "query" => Opts(query: false),
        "catalog" => Opts(catalog: false),
        "access" => Opts(access: false),
        _ => throw new ArgumentOutOfRangeException(nameof(which))
    };

    [Theory]
    [MemberData(nameof(Refused))]
    [Trait("Category", "Security")]
    public async Task ST_115_12_A_refused_branch_equals_the_unknown_endpoint_result_and_calls_no_service(string which, string endpoint, string method)
    {
        var rig = Build(Closed(which));
        var args = new Dictionary<string, object?> { ["sql"] = "select 1" };

        var refused = await rig.Dispatcher.InvokeApiAsync(endpoint, method, args);
        var unknown = await rig.Dispatcher.InvokeApiAsync(Never, method, args);

        Echo(refused, endpoint).ShouldBe(Echo(unknown, Never));
        refused.ShouldContain("EndpointNotFound");
        rig.Data.ReceivedCalls().ShouldBeEmpty();
        rig.Catalog.ReceivedCalls().ShouldBeEmpty();
        rig.Sql.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_12_A_DisabledTools_entry_does_not_close_the_dispatcher_branches()
    {
        // DisabledTools names tools, not endpoints: the dispatcher follows the group and REST switches only.
        var rig = Build(Opts(disabled: ["query_sql"]));

        await rig.Dispatcher.InvokeApiAsync("/api/v1/data/d/s/t", "GET");

        rig.Data.ReceivedCalls().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Groups_never_widen_each_other_Api_on_Query_off_still_answers_catalog_but_no_rows()
    {
        var rig = Build(Opts(query: false));

        await rig.Dispatcher.InvokeApiAsync("/api/v1/catalog/datasets", "GET");
        await rig.Dispatcher.InvokeApiAsync("/api/v1/data/d/s/t", "GET");
        await rig.Dispatcher.InvokeApiAsync("/api/v1/statement", "POST", new Dictionary<string, object?> { ["sql"] = "select 1" });

        rig.Catalog.ReceivedCalls().ShouldNotBeEmpty();
        rig.Data.ReceivedCalls().ShouldBeEmpty();
        rig.Sql.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task REST_switch_off_with_Query_on_still_closes_the_data_branch_but_not_the_statement_branch()
    {
        var rig = Build(Opts(rest: false));

        await rig.Dispatcher.InvokeApiAsync("/api/v1/data/d/s/t", "GET");
        await rig.Dispatcher.InvokeApiAsync("/api/v1/statement", "POST", new Dictionary<string, object?> { ["sql"] = "select 1" });

        rig.Data.ReceivedCalls().ShouldBeEmpty();
        rig.Sql.ReceivedCalls().ShouldNotBeEmpty();
    }

    // ---- describe_api ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("rest")]
    [InlineData("query")]
    [Trait("Category", "Security")]
    public async Task ST_115_13_describe_api_for_a_disabled_data_endpoint_equals_a_never_existing_endpoint(string which)
    {
        var rig = Build(Closed(which));

        foreach (var e in new[] { "/api/v1/data/d.s.t", "/API/V1/DATA/x", "/api/v1/data" })
        {
            var disabled = await rig.Dispatcher.DescribeApiAsync(e, "GET");
            var unknown = await rig.Dispatcher.DescribeApiAsync(Never, "GET");
            Echo(disabled, e).ShouldBe(Echo(unknown, Never));
            disabled.ShouldNotContain("Governed REST Data Query API");
        }
    }

    [Fact]
    public async Task describe_api_by_default_still_documents_the_data_endpoint()
    {
        (await Build(Opts()).Dispatcher.DescribeApiAsync("/api/v1/data/d.s.t", "GET")).ShouldContain("Governed REST Data Query API");
        (await Build(null).Dispatcher.DescribeApiAsync("/api/v1/data/d.s.t", "GET")).ShouldContain("Governed REST Data Query API");
    }

    // ---- Documentation ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Default_documentation_is_byte_identical_to_the_shipped_text()
    {
        foreach (var o in new GatewayOptions?[] { null, Opts() })
        {
            var d = Build(o).Dispatcher;
            (await d.GetEndpointsDocumentationAsync()).ShouldBe(ApiDispatcherGolden.EndpointsDocumentation);
            (await d.GetOpenApiJsonAsync()).ShouldBe(ApiDispatcherGolden.StaticOpenApi);
        }
    }

    [Fact]
    public async Task Default_mcp_tools_documentation_lists_every_dataset_tool()
    {
        var doc = await Build(Opts()).Dispatcher.GetMcpToolsDocumentationAsync();
        var plain = await Build(null).Dispatcher.GetMcpToolsDocumentationAsync();

        doc.ShouldBe(plain);
        foreach (var t in Autheris.Application.Mcp.Services.McpDatasetTools.Definitions()) doc.ShouldContain($"### `{t.Name}`");
    }

    public static IEnumerable<object[]> Docs() =>
    [
        ["rest", new[] { "/api/v1/data" }],
        ["query", new[] { "/api/v1/data", "/api/v1/statement" }],
        ["catalog", new[] { "/api/v1/catalog" }],
        ["access", new[] { "/api/v1/governance/me/access" }]
    ];

    [Theory]
    [MemberData(nameof(Docs))]
    [Trait("Category", "Security")]
    public async Task ST_115_13_Documentation_omits_the_disabled_paths(string which, string[] gone)
    {
        var d = Build(Closed(which)).Dispatcher;
        var endpoints = await d.GetEndpointsDocumentationAsync();
        var openApi = await d.GetOpenApiJsonAsync();

        foreach (var g in gone)
        {
            endpoints.ShouldNotContain(g);
            openApi.ShouldNotContain(g);
        }

        JsonDocument.Parse(openApi).RootElement.GetProperty("paths").ValueKind.ShouldBe(JsonValueKind.Object);
        // Everything that was not closed is still documented.
        if (which != "catalog") endpoints.ShouldContain("/api/v1/catalog/datasets");
        if (which != "access") endpoints.ShouldContain("/api/v1/governance/me/access");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_13_Everything_closed_leaves_valid_documentation_without_any_path()
    {
        var d = Build(Opts(rest: false, catalog: false, query: false, access: false)).Dispatcher;
        var openApi = await d.GetOpenApiJsonAsync();
        var endpoints = await d.GetEndpointsDocumentationAsync();

        JsonDocument.Parse(openApi).RootElement.GetProperty("paths").EnumerateObject().ShouldBeEmpty();
        endpoints.ShouldNotContain("/api/v1/");
    }

    [Theory]
    [InlineData("Query", new[] { "query_sql", "query_graphql", "sample_rows", "query_dataset" })]
    [InlineData("Catalog", new[] { "list_datasets", "describe_dataset", "search_catalog", "list_datasources", "get_data_lineage" })]
    [InlineData("Access", new[] { "get_my_permissions", "describe_my_access" })]
    [InlineData("Api", new[] { "invoke_api", "describe_api" })]
    [Trait("Category", "Security")]
    public async Task ST_115_13_Tool_documentation_lists_only_enabled_tools(string group, string[] gone)
    {
        var o = Opts(
            catalog: group != "Catalog", query: group != "Query", api: group != "Api", access: group != "Access");
        var doc = await Build(o).Dispatcher.GetMcpToolsDocumentationAsync();

        foreach (var g in gone) doc.ShouldNotContain($"`{g}`");
        doc.ShouldContain("# Autheris Model Context Protocol (MCP) Tools Reference");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_13_A_DisabledTools_name_is_removed_from_the_tool_documentation()
    {
        var doc = await Build(Opts(disabled: ["query_sql"])).Dispatcher.GetMcpToolsDocumentationAsync();

        doc.ShouldNotContain("`query_sql`");
        doc.ShouldContain("`query_graphql`");
    }
}
