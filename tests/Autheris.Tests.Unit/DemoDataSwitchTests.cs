using Autheris.Application.Mcp.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>All sample content (demo MCP tools, golden queries, finance/hr GraphQL types) hangs on one switch.</summary>
public class DemoDataSwitchTests
{
    private static GatewayOptions Options(bool? seed) => new() { GovernanceDb = new GovernanceDbOptions { SeedDemoData = seed } };

    [Theory]
    [InlineData(true, "Production", true)]       // explicit wins
    [InlineData(false, "Development", false)]    // explicit wins
    [InlineData(null, "Development", true)]      // default: Development as before
    [InlineData(null, "", false)]                // fail-closed: empty environment is not Development (S-1/D-2)
    [InlineData(null, null, false)]              // fail-closed: null environment is not Development (S-1/D-2)
    [InlineData(null, "Test", false)]            // fail-closed: Test environment is not Development (S-1/D-2)
    [InlineData(null, "Production", false)]
    public void Resolve_ExplicitSettingWinsOtherwiseDevelopment(bool? seed, string? env, bool expected)
    {
        DemoDataSwitch.Resolve(Options(seed), env).ShouldBe(expected);
    }

    [Fact]
    public void McpToolRegistry_WithoutDemoData_HasNoDemoTools_ButKeepsGenericOnes()
    {
        var registry = new McpToolRegistry(Microsoft.Extensions.Options.Options.Create(new GatewayOptions()), new DemoDataSwitch(false));

        var names = registry.GetAvailableTools().Select(t => t.Name).ToList();

        names.ShouldNotContain("query_customers");
        names.ShouldNotContain("query_invoices");
        names.ShouldContain("query_data_catalog");
        names.ShouldContain("simulate_query");
        names.ShouldContain("get_golden_queries");
    }

    [Fact]
    public void McpToolRegistry_WithDemoDataOrNoSwitch_HasDemoTools()
    {
        foreach (var registry in new[]
                 {
                     new McpToolRegistry(Microsoft.Extensions.Options.Options.Create(new GatewayOptions()), new DemoDataSwitch(true)),
                     new McpToolRegistry(Microsoft.Extensions.Options.Options.Create(new GatewayOptions()))
                 })
        {
            var names = registry.GetAvailableTools().Select(t => t.Name).ToList();
            names.ShouldContain("query_customers");
            names.ShouldContain("query_invoices");
        }
    }

    [Fact]
    public async Task GoldenQueryService_BuiltInFinanceQueries_OnlyWithDemoData()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new GatewayOptions());

        var off = new GoldenQueryService(opts, NullLogger<GoldenQueryService>.Instance, new DemoDataSwitch(false));
        var on = new GoldenQueryService(opts, NullLogger<GoldenQueryService>.Instance, new DemoDataSwitch(true));

        (await off.GetByIdAsync("golden_customers_active")).ShouldBeNull();
        (await on.GetByIdAsync("golden_customers_active")).ShouldNotBeNull();
    }
}
