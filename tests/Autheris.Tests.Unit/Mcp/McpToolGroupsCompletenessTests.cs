namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Mcp.Tools;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// PLAN-SEC-115 WP-1 (SEC-115-09, ST-115-21): the group table must classify every tool the registry can hold. A new tool without a
/// decision fails CI before the runtime fallback (unknown name = <c>Query</c>) ever matters.
/// </summary>
public sealed partial class McpToolGroupsCompletenessTests
{
    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex ToolNamePattern();

    private static readonly string[] AllowedOperations = ["List Orders", "Top Customers"];

    private static McpToolRegistry BuildFullRegistry() =>
        new(Options.Create(new GatewayOptions { Mcp = new McpOptions { AllowedOperations = [.. AllowedOperations] } }));

    /// <summary>The guard under test: names that are neither in the table nor an allowed-operation (dynamic) name.</summary>
    internal static IReadOnlyList<string> FindUnclassified(IEnumerable<string> registered, IEnumerable<string> dynamicNames)
    {
        var dynamic = new HashSet<string>(dynamicNames, StringComparer.OrdinalIgnoreCase);
        return registered.Where(n => !McpToolGroups.IsKnownBuiltIn(n) && !dynamic.Contains(n)).ToList();
    }

    private static IEnumerable<string> DerivedDynamicNames() => AllowedOperations.Select(o => o.ToLowerInvariant().Replace(' ', '_'));

    [Fact]
    [Trait("Category", "Security")]
    public void ST_115_21_a_Every_registered_tool_is_classified_or_dynamic()
    {
        var names = BuildFullRegistry().GetAvailableTools().Select(t => t.Name).ToList();

        names.ShouldNotBeEmpty();
        FindUnclassified(names, DerivedDynamicNames()).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ST_115_21_b_Every_public_tool_name_constant_is_in_the_table()
    {
        var holders = new[] { typeof(McpDatasetTools), typeof(McpSemanticTools), typeof(McpDocTools), typeof(McpAdminTools) };
        var constants = holders
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(v => ToolNamePattern().IsMatch(v))
            .ToList();

        constants.Count.ShouldBeGreaterThan(25);
        constants.Where(c => !McpToolGroups.IsKnownBuiltIn(c)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ST_115_21_c_Every_table_entry_exists_in_the_registry()
    {
        var registered = BuildFullRegistry().GetAvailableTools().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        McpToolGroups.BuiltInNames.Where(n => !registered.Contains(n)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void ST_115_21_d_The_guard_can_fail_a_new_unclassified_tool_is_reported()
    {
        var names = BuildFullRegistry().GetAvailableTools().Select(t => t.Name).Append("brand_new_tool").ToList();

        FindUnclassified(names, DerivedDynamicNames()).ShouldBe(["brand_new_tool"]);
    }

    [Theory]
    [InlineData("list_datasets", McpToolGroup.Catalog)]
    [InlineData("describe_dataset", McpToolGroup.Catalog)]
    [InlineData("search_catalog", McpToolGroup.Catalog)]
    [InlineData("list_datasources", McpToolGroup.Catalog)]
    [InlineData("get_data_lineage", McpToolGroup.Catalog)]
    [InlineData("query_data_catalog", McpToolGroup.Catalog)]
    [InlineData("list_metrics", McpToolGroup.Catalog)]
    [InlineData("describe_metric", McpToolGroup.Catalog)]
    [InlineData("get_golden_queries", McpToolGroup.Catalog)]
    [InlineData("query_dataset", McpToolGroup.Query)]
    [InlineData("query_sql", McpToolGroup.Query)]
    [InlineData("query_graphql", McpToolGroup.Query)]
    [InlineData("sample_rows", McpToolGroup.Query)]
    [InlineData("query_metrics", McpToolGroup.Query)]
    [InlineData("simulate_query", McpToolGroup.Query)]
    [InlineData("query_customers", McpToolGroup.Query)]
    [InlineData("query_invoices", McpToolGroup.Query)]
    [InlineData("invoke_api", McpToolGroup.Api)]
    [InlineData("describe_api", McpToolGroup.Api)]
    [InlineData("get_my_permissions", McpToolGroup.Access)]
    [InlineData("describe_my_access", McpToolGroup.Access)]
    [InlineData("admin_plan_access", McpToolGroup.None)]
    [InlineData("search_docs", McpToolGroup.None)]
    [InlineData("deposit_documentation", McpToolGroup.None)]
    [InlineData("QUERY_SQL", McpToolGroup.Query)]
    [InlineData("Invoke_Api", McpToolGroup.Api)]
    public void Mapping_matches_the_plan_table_case_insensitively(string name, McpToolGroup expected)
    {
        McpToolGroups.Of(name).ShouldBe(expected);
        McpToolGroups.IsKnownBuiltIn(name).ShouldBeTrue();
    }

    [Theory]
    [InlineData("some_dynamic_tool")]
    [InlineData("")]
    [InlineData("query_sql\0")]
    [InlineData("ｑｕｅｒｙ_ｓｑｌ")]
    public void Unknown_names_resolve_to_Query_fail_safe(string name)
    {
        McpToolGroups.Of(name).ShouldBe(McpToolGroup.Query);
        McpToolGroups.IsKnownBuiltIn(name).ShouldBeFalse();
    }
}
