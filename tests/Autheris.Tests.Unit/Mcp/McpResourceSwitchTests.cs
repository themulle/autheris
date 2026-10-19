namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Mcp;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Interfaces;
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
        ["autheris://api/docs/mcp-tools", McpToolGroup.Api]
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
            "autheris://api/openapi.json", "autheris://api/docs/endpoints", "autheris://api/docs/mcp-tools"
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
        uris.ShouldNotContain(u => u.StartsWith(prefix, StringComparison.Ordinal));
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
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([Table()]));

        var open = new SemanticMcpCompiler(repo, NullLogger<SemanticMcpCompiler>.Instance, switches: Switches());
        (await open.GetSemanticResourcesAsync()).ShouldNotBeEmpty();

        repo.ClearReceivedCalls();
        var closed = new SemanticMcpCompiler(repo, NullLogger<SemanticMcpCompiler>.Instance, switches: Switches(Off(McpToolGroup.Catalog)));
        (await closed.GetSemanticResourcesAsync()).ShouldBeEmpty();
        repo.ReceivedCalls().ShouldBeEmpty();

        // JSON-RPC handler: list is empty and read equals the answer for an unknown uri.
        var registry = new McpToolRegistry();
        var guardrail = new AiDataGuardrailService(registry, Microsoft.Extensions.Options.Options.Create(new GatewayOptions()), NullLogger<AiDataGuardrailService>.Instance, Substitute.For<IAuditLogRepository>());
        var handler = new McpProtocolHandler(new McpSessionStore(NullLogger<McpSessionStore>.Instance), registry, guardrail, NullLogger<McpProtocolHandler>.Instance, closed);
        var sid = handler.CreateSession("agent", "t").SessionId;

        var list = await handler.HandleMessageAsync(sid, """{"jsonrpc":"2.0","id":1,"method":"resources/list"}""");
        System.Text.Json.JsonDocument.Parse(list).RootElement.GetProperty("result").GetProperty("resources").GetArrayLength().ShouldBe(0);

        string Read(string uri) => $$$"""{"jsonrpc":"2.0","id":2,"method":"resources/read","params":{"uri":"{{{uri}}}"}}""";
        var a = await handler.HandleMessageAsync(sid, Read("glossary://finance/mrr"));
        var b = await handler.HandleMessageAsync(sid, Read("glossary://nothing/here"));
        a.Replace("glossary://finance/mrr", "<URI>", StringComparison.Ordinal).ShouldBe(b.Replace("glossary://nothing/here", "<URI>", StringComparison.Ordinal));
    }
}
