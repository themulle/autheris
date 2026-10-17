namespace Autheris.Tests.Unit.Mcp;

using Autheris.Application.Mcp.Services;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

/// <summary>PLAN-SEC-115 A-9 / test 6 and 22: the startup read-path line is derived from the effective options.</summary>
public sealed class ReadPathInventoryTests
{
    [Fact]
    public void Default_profile_golden_line()
    {
        ReadPathInventory.Describe(new GatewayOptions()).ShouldBe(
            "Read paths: graphql=on rest_data=on odata=on arrow=on flight_sql=on duckdb_olap=on websql=off sql_endpoints=on lakehouse=off mcp=off(catalog=on,query=on,api=on,access=on,disabled_tools=0)");
    }

    [Fact]
    public void Hardened_profile_golden_line_reflects_the_effective_switches()
    {
        var options = new GatewayOptions
        {
            RestApi = new RestApiOptions { DataEndpoint = new RestDataEndpointOptions { Enabled = false } },
            OData = new ODataOptions { Enabled = false },
            Arrow = new ArrowExportOptions { Enabled = false },
            DuckDbOlap = new DuckDbOlapOptions { Enabled = false },
            Mcp = new McpOptions
            {
                Enabled = true,
                Tools = new McpToolGroupOptions { Query = false },
                DisabledTools = ["query_sql", "admin_apply_access"]
            }
        };

        var line = ReadPathInventory.Describe(options);

        line.ShouldContain("rest_data=off");
        line.ShouldContain("odata=off");
        line.ShouldContain("arrow=off");
        line.ShouldContain("duckdb_olap=off");
        line.ShouldContain("mcp=on(catalog=on,query=off,api=on,access=on,disabled_tools=2)");
    }

    [Fact]
    public void Line_contains_no_tool_names_or_secrets()
    {
        var line = ReadPathInventory.Describe(new GatewayOptions { Mcp = new McpOptions { DisabledTools = ["query_sql"] } });

        line.ShouldNotContain("query_sql");
        line.ShouldNotContain("/");
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public System.Collections.Generic.List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}|{formatter(state, exception)}");
    }

    [Fact]
    public void ST_115_22_The_startup_line_is_logged_once_at_Information_with_the_effective_switches()
    {
        var logger = new RecordingLogger();
        var options = new GatewayOptions
        {
            RestApi = new RestApiOptions { DataEndpoint = new RestDataEndpointOptions { Enabled = false } },
            Mcp = new McpOptions { Enabled = true, Tools = new McpToolGroupOptions { Query = false } }
        };

        Autheris.Api.Extensions.GatewayApplicationBuilderExtensions.LogReadPathInventory(logger, options);

        logger.Lines.Count.ShouldBe(1);
        logger.Lines[0].ShouldStartWith("Information|Read paths:");
        logger.Lines[0].ShouldContain("rest_data=off");
        logger.Lines[0].ShouldContain("mcp=on(catalog=on,query=off,api=on,access=on,disabled_tools=0)");
    }
}
