namespace Autheris.Tests.Unit.Mcp;

using System;
using Autheris.Tests.Common.Fakes;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Mcp;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// PLAN-SEC-115 WP-2 (A-6, C-115-6, C-115-10): resources, semantic resources and prompts follow the group of the tool that renders the
/// same data. A switched-off resource is unlisted and answers exactly like an unknown URI; the gate is the first statement of
/// <c>ReadAsync</c>, so a closed resource never reaches a catalog or dispatcher call.
/// </summary>
public sealed class McpResourceSwitchTests
{
    private static readonly McpSessionContext Session = new(
        "s-1", "sp-1", "tenant-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, UserSid: "S-1-5-21-1", Roles: ["Analyst"]);

    private static McpToolGroupOptions Off(McpToolGroup g) => new()
    {
        Catalog = g != McpToolGroup.Catalog, Query = g != McpToolGroup.Query, Api = g != McpToolGroup.Api, Access = g != McpToolGroup.Access
    };

    private static ReadPathSwitches Switches(McpToolGroupOptions? tools = null) =>
        ReadPathSwitches.From(new GatewayOptions { Mcp = new McpOptions { Tools = tools ?? new McpToolGroupOptions() } });

    private static (McpResourceProvider Provider, ICatalogDiscoveryService Discovery, IApiDispatcherService Dispatcher) Provider(IReadPathSwitches? switches)
    {
        var discovery = Substitute.For<ICatalogDiscoveryService>();
        var dispatcher = Substitute.For<IApiDispatcherService>();
        dispatcher.GetOpenApiJsonAsync(Arg.Any<CancellationToken>()).Returns("{}");
        dispatcher.GetEndpointsDocumentationAsync(Arg.Any<CancellationToken>()).Returns("docs");
        dispatcher.GetMcpToolsDocumentationAsync(Arg.Any<CancellationToken>()).Returns("tools");
        dispatcher.GetQueryGuideDocumentationAsync(Arg.Any<CancellationToken>()).Returns("guide");
        return (new McpResourceProvider(discovery, dispatcher, switches), discovery, dispatcher);
    }

    public static IEnumerable<object[]> NativeResources() =>
    [
        ["autheris://catalog/summary", McpToolGroup.Catalog],
        ["autheris://catalog/datasources", McpToolGroup.Catalog],
        ["autheris://catalog/datasets/sales.public.orders/schema", McpToolGroup.Catalog],
        ["autheris://governance/my-access", McpToolGroup.Access],
        ["autheris://api/openapi.json", McpToolGroup.Api],
        ["autheris://api/docs/endpoints", McpToolGroup.Api],
        ["autheris://api/docs/mcp-tools", McpToolGroup.Api],
        ["autheris://api/docs/query-guide", McpToolGroup.Query]
    ];

    [Theory]
    [MemberData(nameof(NativeResources))]
    public async Task By_default_every_native_resource_is_readable(string uri, McpToolGroup _)
    {
        var (provider, _, _) = Provider(Switches());

        provider.IsResourceEnabled(uri).ShouldBeTrue();
        // The schema resource of an unknown dataset is "not found" on its own; every other URI must render.
        if (!uri.Contains("/datasets/", StringComparison.Ordinal))
        {
            (await provider.ReadAsync(uri, Session)).Uri.ShouldBe(uri);
        }
    }

    [Theory]
    [MemberData(nameof(NativeResources))]
    [Trait("Category", "Security")]
    public async Task ST_115_15_A_disabled_resource_is_not_found_like_an_unknown_one_and_calls_nothing(string uri, McpToolGroup group)
    {
        var (provider, discovery, dispatcher) = Provider(Switches(Off(group)));
        provider.IsResourceEnabled(uri).ShouldBeFalse();

        var disabled = await Should.ThrowAsync<McpResourceNotFoundException>(async () => await provider.ReadAsync(uri, Session));
        var (plain, _, _) = Provider(Switches());
        var unknownUri = uri[..uri.LastIndexOf('/')] + "/does-not-exist-xyz";
        var unknown = await Should.ThrowAsync<McpResourceNotFoundException>(async () => await plain.ReadAsync(unknownUri, Session));

        disabled.Message.Replace(uri, "<URI>", StringComparison.Ordinal).ShouldBe(unknown.Message.Replace(unknownUri, "<URI>", StringComparison.Ordinal));
        discovery.ReceivedCalls().ShouldBeEmpty();
        dispatcher.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Disabling_one_group_leaves_the_other_groups_resources_readable()
    {
        var (provider, _, _) = Provider(Switches(Off(McpToolGroup.Catalog)));

        (await provider.ReadAsync("autheris://governance/my-access", Session)).Text.ShouldContain("S-1-5-21-1");
        (await provider.ReadAsync("autheris://api/docs/endpoints", Session)).Text.ShouldBe("docs");
    }

    [Fact]
    public async Task The_SDK_read_path_maps_a_disabled_resource_to_the_same_ResourceNotFound_as_an_unknown_one()
    {
        var (provider, _, _) = Provider(Switches(Off(McpToolGroup.Api)));

        var disabled = await Should.ThrowAsync<McpProtocolException>(async () =>
            await GatewayMcpServer.ReadNativeResourceAsync("autheris://api/openapi.json", provider, Session));
        var unknown = await Should.ThrowAsync<McpProtocolException>(async () =>
            await GatewayMcpServer.ReadNativeResourceAsync("autheris://api/unknown.json", provider, Session));

        disabled.ErrorCode.ShouldBe(unknown.ErrorCode);
        disabled.Message.Replace("autheris://api/openapi.json", "<URI>", StringComparison.Ordinal)
            .ShouldBe(unknown.Message.Replace("autheris://api/unknown.json", "<URI>", StringComparison.Ordinal));
    }

    // ---- SDK listings ----------------------------------------------------------------------------------------------------------

    private static string[] ListedUris(IReadPathSwitches? switches) =>
        GatewayMcpServer.FilterResources(GatewayMcpServer.NativeResourceList(), switches).Select(r => r.Uri!).ToArray();

    [Fact]
    public void Default_resource_list_is_unchanged_and_complete()
    {
        ListedUris(null).ShouldBe(
        [
            "autheris://catalog/summary", "autheris://catalog/datasources", "autheris://governance/my-access",
            "autheris://api/openapi.json", "autheris://api/docs/endpoints", "autheris://api/docs/mcp-tools",
            "autheris://api/docs/query-guide"
        ]);
        ListedUris(Switches()).ShouldBe(ListedUris(null));
    }

    [Theory]
    [InlineData(McpToolGroup.Catalog, "autheris://catalog/")]
    [InlineData(McpToolGroup.Access, "autheris://governance/")]
    [InlineData(McpToolGroup.Api, "autheris://api/")]
    [Trait("Category", "Security")]
    public void ST_115_11_A_closed_group_removes_its_resources_from_the_list(McpToolGroup group, string prefix)
    {
        var uris = ListedUris(Switches(Off(group)));

        uris.ShouldNotBeEmpty();
        uris.Where(u => u != McpResourceProvider.QueryGuideDocUri).ShouldNotContain(u => u.StartsWith(prefix, StringComparison.Ordinal));
        // The query guide follows the query tools, so closing the Api group keeps it.
        uris.ShouldContain(McpResourceProvider.QueryGuideDocUri);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ST_115_11_Resource_templates_follow_the_Catalog_group()
    {
        var all = GatewayMcpServer.FilterResourceTemplates(GatewayMcpServer.NativeResourceTemplateList(), null);
        all.Select(t => t.Name).ShouldBe(["dataset", "dataset_schema"]);

        GatewayMcpServer.FilterResourceTemplates(GatewayMcpServer.NativeResourceTemplateList(), Switches(Off(McpToolGroup.Catalog))).ShouldBeEmpty();
        GatewayMcpServer.FilterResourceTemplates(GatewayMcpServer.NativeResourceTemplateList(), Switches(Off(McpToolGroup.Api))).Count.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ST_115_11_The_dataset_template_and_resource_follow_a_DisabledTools_entry_of_describe_dataset()
    {
        var switches = ReadPathSwitches.From(new GatewayOptions { Mcp = new McpOptions { DisabledTools = ["describe_dataset"] } });

        switches.IsResourceEnabled("autheris://datasets/a.b.c").ShouldBeFalse();
        switches.IsResourceEnabled("autheris://catalog/summary").ShouldBeTrue();
        GatewayMcpServer.FilterResourceTemplates(GatewayMcpServer.NativeResourceTemplateList(), switches).Select(t => t.Name).ShouldBe(["dataset_schema"]);
    }

    // ---- Prompts (ST-115-14) ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Both_prompts_are_listed_by_default()
    {
        GatewayMcpServer.GetAvailablePrompts(Switches()).Select(p => p.Name).ShouldBe(["explore_dataset", "audit_access_compliance"]);
        GatewayMcpServer.GetAvailablePrompts().Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(McpToolGroup.Query, "explore_dataset")]
    [InlineData(McpToolGroup.Catalog, "explore_dataset")]
    [InlineData(McpToolGroup.Catalog, "audit_access_compliance")]
    [InlineData(McpToolGroup.Access, "audit_access_compliance")]
    [Trait("Category", "Security")]
    public void ST_115_14_A_gated_prompt_is_unlisted_and_answers_like_an_unknown_prompt(McpToolGroup group, string prompt)
    {
        var switches = Switches(Off(group));

        GatewayMcpServer.GetAvailablePrompts(switches).Select(p => p.Name).ShouldNotContain(prompt);

        var gated = Should.Throw<McpProtocolException>(() => GatewayMcpServer.BuildPrompt(prompt, new Dictionary<string, string?>(), switches));
        var unknown = Should.Throw<McpProtocolException>(() => GatewayMcpServer.BuildPrompt("no_such_prompt", new Dictionary<string, string?>(), switches));
        gated.ErrorCode.ShouldBe(unknown.ErrorCode);
        gated.Message.Replace(prompt, "<P>", StringComparison.Ordinal).ShouldBe(unknown.Message.Replace("no_such_prompt", "<P>", StringComparison.Ordinal));
    }

    [Fact]
    public void Prompts_without_switches_still_build_as_before()
    {
        GatewayMcpServer.BuildPrompt("explore_dataset", new Dictionary<string, string?> { ["dataset"] = "a.b.c" }).Messages.ShouldNotBeEmpty();
    }

    // ---- Semantic resources -------------------------------------------------------------------------------------------------------

    private static TableMetadata Table() => new()
    {
        Identifier = new TableIdentifier("finance", "dbo", "mrr"),
        Table = new Table { SchemaName = "dbo", TableName = "mrr", Description = "Revenue" },
        PrimaryKeyColumns = ["id"],
        Columns = [new TableColumn { ColumnName = "id", DataType = "integer" }]
    };

    [Fact]
    [Trait("Category", "Security")]
    public async Task ST_115_11_Semantic_resources_are_empty_when_Catalog_is_off_in_both_handlers()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.StubAllTables(Task.FromResult<IReadOnlyList<TableMetadata>>([Table()]));
        Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.ServeCatalogReads(repo);

        var open = new SemanticMcpCompiler(repo, NullLogger<SemanticMcpCompiler>.Instance, options: Microsoft.Extensions.Options.Options.Create(new Autheris.Domain.Options.GatewayOptions { Insecure = new Autheris.Domain.Options.InsecureGettingStartedOptions { danger_bypass_mcp_auth = true } }), switches: Switches());
        (await open.GetSemanticResourcesAsync()).ShouldNotBeEmpty();

        repo.ClearReceivedCalls();
        var closed = new SemanticMcpCompiler(repo, NullLogger<SemanticMcpCompiler>.Instance, switches: Switches(Off(McpToolGroup.Catalog)));
        (await closed.GetSemanticResourcesAsync()).ShouldBeEmpty();
        repo.ReceivedCalls().ShouldBeEmpty();

        // Live SDK server path: reading a gated semantic resource fails identically to an unknown URI.
        var registry = new McpToolRegistry();
        var guardrail = new AiDataGuardrailService(registry, Microsoft.Extensions.Options.Options.Create(new GatewayOptions()), NullLogger<AiDataGuardrailService>.Instance, new InMemoryAuditLogRepository(), decisionPoint: Autheris.Tests.Unit.TestSupport.PdpTestDoubles.AllowAll(), tableMetadataRepository: Autheris.Tests.Unit.TestSupport.PdpTestDoubles.AnyTable());
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, "S-1-USER"), new System.Security.Claims.Claim("tenant_id", "t")], "Bearer"))
        };
        http.Items[Autheris.Domain.Security.SecurityPrincipalContext.TenantIdItemKey] = new TenantId("t");
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(services, new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = http });
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, Microsoft.Extensions.Options.Options.Create(new GatewayOptions { Mcp = new McpOptions { Enabled = true } }));
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IAiDataGuardrailService>(services, guardrail);
        await using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);

        var a = await Should.ThrowAsync<McpProtocolException>(() => GatewayMcpServer.ReadGuardedSemanticResourceAsync(sp, "glossary://finance/mrr", closed, CancellationToken.None));
        var b = await Should.ThrowAsync<McpProtocolException>(() => GatewayMcpServer.ReadGuardedSemanticResourceAsync(sp, "glossary://nothing/here", closed, CancellationToken.None));
        a.ErrorCode.ShouldBe(b.ErrorCode);
        a.Message.Replace("glossary://finance/mrr", "<URI>", StringComparison.Ordinal).ShouldBe(b.Message.Replace("glossary://nothing/here", "<URI>", StringComparison.Ordinal));
    }

    // ---- Query guide and hint surfaces follow the enabled tools ----------------------------------------------------------------

    private static ReadPathSwitches SwitchesOf(McpToolGroupOptions? tools = null, string[]? disabled = null, bool restData = true) =>
        ReadPathSwitches.From(new GatewayOptions
        {
            Mcp = new McpOptions { Tools = tools ?? new McpToolGroupOptions(), DisabledTools = disabled?.ToList() },
            RestApi = new RestApiOptions { DataEndpoint = new RestDataEndpointOptions { Enabled = restData } }
        });

    private static IReadOnlyList<CatalogDatasetSummary> Datasets(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new CatalogDatasetSummary($"d{i % 3}.public.t{i}", $"d{i % 3}", "public", $"t{i}", "sql", "internal", "x", true))];

    private static async Task<string> SummaryAsync(IReadPathSwitches? switches, int datasets = 51)
    {
        var (provider, discovery, _) = Provider(switches);
        discovery.ListDatasetsAsync(Arg.Any<RequestContext>(), Arg.Any<CancellationToken>()).Returns(Datasets(datasets));
        return (await provider.ReadAsync("autheris://catalog/summary", Session)).Text;
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_1209_Guide_read_when_no_query_tool_matches_unknown_uri()
    {
        foreach (var switches in new[] { SwitchesOf(Off(McpToolGroup.Query)), SwitchesOf(disabled: ["query_graphql", "query_sql", "query_dataset"]) })
        {
            var (provider, discovery, dispatcher) = Provider(switches);
            var guide = McpResourceProvider.QueryGuideDocUri;
            var unknownUri = "autheris://api/docs/unknown";

            var closed = await Should.ThrowAsync<McpResourceNotFoundException>(async () => await provider.ReadAsync(guide, Session));
            var (plain, _, _) = Provider(Switches());
            var unknown = await Should.ThrowAsync<McpResourceNotFoundException>(async () => await plain.ReadAsync(unknownUri, Session));

            closed.Message.Replace(guide, "<URI>", StringComparison.Ordinal).ShouldBe(unknown.Message.Replace(unknownUri, "<URI>", StringComparison.Ordinal));
            discovery.ReceivedCalls().ShouldBeEmpty();
            dispatcher.ReceivedCalls().ShouldBeEmpty();
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SEC_1209_Guide_not_listed_when_no_query_tool_enabled()
    {
        ListedUris(SwitchesOf(Off(McpToolGroup.Query))).ShouldNotContain(McpResourceProvider.QueryGuideDocUri);
        ListedUris(SwitchesOf(new McpToolGroupOptions { Catalog = false, Query = false, Api = false, Access = false })).ShouldBeEmpty();
        ListedUris(SwitchesOf(Off(McpToolGroup.Api))).ShouldContain(McpResourceProvider.QueryGuideDocUri);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SEC_1209_Catalog_summary_tip_names_only_enabled_tools()
    {
        var all = await SummaryAsync(SwitchesOf());
        all.ShouldContain("`find_relevant_tables` or `search_catalog`");
        all.ShouldContain("`describe_dataset`");
        all.ShouldNotContain("brief");

        var schemaOnly = await SummaryAsync(SwitchesOf(disabled: ["find_relevant_tables", "search_catalog", "describe_dataset"]));
        schemaOnly.ShouldContain("`get_table_schema`");
        schemaOnly.ShouldNotContain("find_relevant_tables");
        schemaOnly.ShouldNotContain("describe_dataset");

        var none = await SummaryAsync(SwitchesOf(disabled: ["find_relevant_tables", "search_catalog", "describe_dataset", "get_table_schema"]));
        none.ShouldNotContain("Tip");
        none.ShouldContain("Total Datasets: 51");

        (await SummaryAsync(null, 51)).ShouldNotContain("Tip");
    }

    public static IEnumerable<object[]> HintSets() =>
    [
        ["defaults", new McpToolGroupOptions(), Array.Empty<string>(), true],
        ["api off", Off(McpToolGroup.Api), Array.Empty<string>(), true],
        ["query off", Off(McpToolGroup.Query), Array.Empty<string>(), true],
        ["all groups off", new McpToolGroupOptions { Catalog = false, Query = false, Api = false, Access = false }, Array.Empty<string>(), true],
        ["access off", Off(McpToolGroup.Access), Array.Empty<string>(), true],
        ["sql only", new McpToolGroupOptions(), new[] { "query_graphql", "query_dataset" }, true],
        ["no discovery or schema tools", new McpToolGroupOptions(), new[] { "describe_dataset", "get_my_permissions", "find_relevant_tables", "get_table_schema", "sample_rows", "get_data_lineage", "search_catalog" }, true],
        ["rest data endpoint off", new McpToolGroupOptions(), Array.Empty<string>(), false]
    ];

    [Theory]
    [MemberData(nameof(HintSets))]
    [Trait("Category", "Security")]
    public async Task SEC_1209_No_disabled_tool_is_named_in_hint_surfaces(string label, McpToolGroupOptions groups, string[] disabledTools, bool restData)
    {
        var switches = SwitchesOf(groups, disabledTools, restData);
        var texts = new Dictionary<string, string> { ["instructions"] = McpDatasetTools.BuildServerInstructions(switches) };
        var args = new Dictionary<string, string?> { ["dataset"] = "sales.public.orders" };
        foreach (var prompt in new[] { "explore_dataset", "audit_access_compliance" })
        {
            if (switches.IsPromptEnabled(prompt))
            {
                texts[prompt] = ((TextContentBlock)GatewayMcpServer.BuildPrompt(prompt, args, switches).Messages[0].Content).Text;
            }
        }

        if (switches.IsResourceEnabled("autheris://catalog/summary"))
        {
            texts["summary"] = await SummaryAsync(switches);
        }

        foreach (var r in GatewayMcpServer.FilterResources(GatewayMcpServer.NativeResourceList(), switches))
        {
            texts["resource " + r.Uri] = r.Name + " " + r.Description;
        }

        texts["query_graphql description"] = McpDatasetTools.Definitions(switches).Single(t => t.Name == McpDatasetTools.QueryGraphQl).Description;

        var guideEnabled = switches.IsResourceEnabled(McpResourceProvider.QueryGuideDocUri);
        if (guideEnabled)
        {
            var dispatcher = new ApiDispatcherService(gatewayOptions: Microsoft.Extensions.Options.Options.Create(new GatewayOptions()), switches: switches);
            texts["guide"] = await dispatcher.GetQueryGuideDocumentationAsync(CancellationToken.None);
        }

        var forbidden = McpToolGroups.BuiltInNames.Where(n => !switches.IsToolEnabled(n)).ToList();
        if (!restData) forbidden.Add(McpDatasetTools.QueryDataset);
        forbidden.AddRange(
        [
            "request_access", "admin_plan_access", "admin_apply_access", "admin_register_datasource",
            "admin_set_dataset_state", "admin_resolve_principal", "admin_set_dataset_catalog"
        ]);
        foreach (var (surface, text) in texts)
        {
            foreach (var name in forbidden)
            {
                System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(name)}\b")
                    .ShouldBeFalse($"{label}: {name} is named in {surface}");
            }

            // No catalog-derived number and no brief recommendation in the startup text.
            if (surface == "instructions")
            {
                text.ShouldNotContain("brief: true");
            }
        }

        texts["instructions"].Contains(McpResourceProvider.QueryGuideDocUri, StringComparison.Ordinal).ShouldBe(guideEnabled, label);
        texts.Keys.Any(k => k == "resource " + McpResourceProvider.QueryGuideDocUri).ShouldBe(guideEnabled, label);
    }
}
