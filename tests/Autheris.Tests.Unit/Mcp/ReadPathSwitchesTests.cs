namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

/// <summary>PLAN-SEC-115 WP-1: the immutable startup snapshot behind every read-path switch.</summary>
public sealed class ReadPathSwitchesTests
{
    private static ReadPathSwitches From(McpToolGroupOptions? tools = null, string[]? disabled = null, bool restData = true) =>
        ReadPathSwitches.From(new GatewayOptions
        {
            Mcp = new McpOptions { Tools = tools ?? new McpToolGroupOptions(), DisabledTools = disabled?.ToList() },
            RestApi = new RestApiOptions { DataEndpoint = new RestDataEndpointOptions { Enabled = restData } }
        });

    [Fact]
    public void Defaults_all_enabled()
    {
        var s = From();

        s.RestDataEndpointEnabled.ShouldBeTrue();
        foreach (var g in Enum.GetValues<McpToolGroup>()) s.IsGroupEnabled(g).ShouldBeTrue();
        foreach (var n in McpToolGroups.BuiltInNames) { s.IsToolEnabled(n).ShouldBeTrue(); s.DisabledReason(n).ShouldBeNull(); }
        s.IsToolEnabled("dynamic_tool").ShouldBeTrue();
    }

    [Fact]
    public void Rest_switch_is_taken_from_the_options()
    {
        From(restData: false).RestDataEndpointEnabled.ShouldBeFalse();
    }

    [Theory]
    [InlineData(McpToolGroup.Catalog, "list_datasets")]
    [InlineData(McpToolGroup.Query, "query_sql")]
    [InlineData(McpToolGroup.Api, "invoke_api")]
    [InlineData(McpToolGroup.Access, "get_my_permissions")]
    public void Group_off_disables_its_members_only(McpToolGroup group, string member)
    {
        var tools = new McpToolGroupOptions
        {
            Catalog = group != McpToolGroup.Catalog, Query = group != McpToolGroup.Query,
            Api = group != McpToolGroup.Api, Access = group != McpToolGroup.Access
        };
        var s = From(tools);

        s.IsGroupEnabled(group).ShouldBeFalse();
        s.IsToolEnabled(member).ShouldBeFalse();
        s.DisabledReason(member).ShouldNotBeNull();
        s.IsToolEnabled("admin_plan_access").ShouldBeTrue();
        s.IsToolEnabled("search_docs").ShouldBeTrue();
    }

    [Fact]
    public void Dynamic_and_unknown_names_follow_the_Query_group()
    {
        var s = From(new McpToolGroupOptions { Query = false });

        s.IsToolEnabled("anything_dynamic").ShouldBeFalse();
        s.IsToolEnabled("list_datasets").ShouldBeTrue();
    }

    [Fact]
    public void DisabledTools_wins_over_an_enabled_group()
    {
        var s = From(disabled: ["query_sql", "admin_apply_access"]);

        s.IsToolEnabled("query_sql").ShouldBeFalse();
        s.IsToolEnabled("admin_apply_access").ShouldBeFalse();
        s.IsToolEnabled("query_dataset").ShouldBeTrue();
        s.DisabledReason("query_sql").ShouldNotBeNull();
    }

    [Fact]
    public void A_group_switch_never_enables_a_listed_tool()
    {
        var s = From(new McpToolGroupOptions { Query = true }, ["sample_rows"]);

        s.IsGroupEnabled(McpToolGroup.Query).ShouldBeTrue();
        s.IsToolEnabled("sample_rows").ShouldBeFalse();
    }

    [Theory]
    [InlineData("QUERY_SQL")]
    [InlineData(" query_sql ")]
    public void Name_matching_is_case_insensitive_and_trimmed(string listed)
    {
        var s = From(disabled: [listed]);

        s.IsToolEnabled("query_sql").ShouldBeFalse();
        s.IsToolEnabled("Query_Sql").ShouldBeFalse();
    }

    [Fact]
    public void Dropped_names_recorded_by_the_registry_get_a_reason_and_the_set_is_bounded()
    {
        var s = From(new McpToolGroupOptions { Query = false });
        s.DisabledReason("never_registered").ShouldBeNull();

        s.RecordDropped("dyn_one");
        s.DisabledReason("dyn_one").ShouldNotBeNull();
        s.DisabledReason("DYN_ONE").ShouldNotBeNull();

        for (int i = 0; i < ReadPathSwitches.MaxDroppedNames + 500; i++) s.RecordDropped($"dyn_{i}");
        s.DroppedCount.ShouldBeLessThanOrEqualTo(ReadPathSwitches.MaxDroppedNames);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ST_115_09_Random_unknown_names_do_not_grow_state_or_get_a_reason()
    {
        var s = From(new McpToolGroupOptions { Query = false });
        var before = s.DroppedCount;

        for (int i = 0; i < 10_000; i++)
        {
            s.IsToolEnabled(Guid.NewGuid().ToString("N")).ShouldBeFalse();
            s.DisabledReason(Guid.NewGuid().ToString("N")).ShouldBeNull();
        }

        s.DroppedCount.ShouldBe(before);
    }

    [Theory]
    [InlineData("autheris://catalog/summary", McpToolGroup.Catalog)]
    [InlineData("autheris://catalog/datasources", McpToolGroup.Catalog)]
    [InlineData("autheris://catalog/datasets/a.b.c/schema", McpToolGroup.Catalog)]
    [InlineData("autheris://datasets/a.b.c", McpToolGroup.Catalog)]
    [InlineData("autheris://governance/my-access", McpToolGroup.Access)]
    [InlineData("autheris://api/openapi.json", McpToolGroup.Api)]
    [InlineData("autheris://api/docs/endpoints", McpToolGroup.Api)]
    [InlineData("autheris://api/docs/mcp-tools", McpToolGroup.Api)]
    public void Resources_follow_the_group_of_the_data_they_render(string uri, McpToolGroup group)
    {
        From().IsResourceEnabled(uri).ShouldBeTrue();
        var tools = new McpToolGroupOptions
        {
            Catalog = group != McpToolGroup.Catalog, Query = true, Api = group != McpToolGroup.Api, Access = group != McpToolGroup.Access
        };
        From(tools).IsResourceEnabled(uri).ShouldBeFalse();
        From(tools).IsResourceEnabled(uri.ToUpperInvariant()).ShouldBeFalse();
    }

    [Fact]
    public void Unknown_resource_uri_is_reported_enabled_so_the_unknown_branch_stays_the_single_not_found_producer()
    {
        From(new McpToolGroupOptions { Catalog = false, Api = false, Access = false }).IsResourceEnabled("autheris://nothing/here").ShouldBeTrue();
    }

    [Fact]
    public void Prompts_need_every_group_of_the_tools_they_mention()
    {
        From().IsPromptEnabled("explore_dataset").ShouldBeTrue();
        From().IsPromptEnabled("audit_access_compliance").ShouldBeTrue();

        From(new McpToolGroupOptions { Query = false }).IsPromptEnabled("explore_dataset").ShouldBeFalse();
        From(new McpToolGroupOptions { Catalog = false }).IsPromptEnabled("explore_dataset").ShouldBeFalse();
        From(new McpToolGroupOptions { Catalog = false }).IsPromptEnabled("audit_access_compliance").ShouldBeFalse();
        From(new McpToolGroupOptions { Access = false }).IsPromptEnabled("audit_access_compliance").ShouldBeFalse();
        From(new McpToolGroupOptions { Query = false }).IsPromptEnabled("audit_access_compliance").ShouldBeTrue();
        From(new McpToolGroupOptions { Query = false, Catalog = false }).IsPromptEnabled("unknown_prompt").ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ST_115_22_Surface_has_no_member_taking_request_scoped_types()
    {
        var forbidden = new HashSet<Type> { typeof(HttpContext), typeof(System.Security.Claims.ClaimsPrincipal), typeof(McpSessionContext), typeof(RequestContext) };

        foreach (var t in new[] { typeof(IReadPathSwitches), typeof(ReadPathSwitches) })
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                m.GetParameters().Where(p => forbidden.Contains(p.ParameterType)).ShouldBeEmpty($"{t.Name}.{m.Name}");
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ST_115_18_Snapshot_is_startup_only_and_takes_no_options_monitor()
    {
        foreach (var c in typeof(ReadPathSwitches).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            c.GetParameters().Select(p => p.ParameterType.Name).ShouldNotContain(n => n.StartsWith("IOptionsMonitor", StringComparison.Ordinal));
        }

        // The snapshot copies the list: a later change of the source does not change the switch.
        var list = new List<string> { "query_sql" };
        var withList = ReadPathSwitches.From(new GatewayOptions { Mcp = new McpOptions { DisabledTools = list } });
        list.Clear();
        withList.IsToolEnabled("query_sql").ShouldBeFalse();
    }
}
