namespace Autheris.Application.Mcp.Services;

using System.Globalization;
using Autheris.Domain.Options;

/// <summary>
/// PLAN-SEC-115 A-9 (FR-115-6): the one-line startup inventory of the read paths, so an operator can verify a hardened profile
/// (for example GraphQL only) from the log. Pure and secret-free: no tool names, no paths, only on/off flags and a count.
/// </summary>
public static class ReadPathInventory
{
    public static string Describe(GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var mcp = options.Mcp ?? new McpOptions();
        var tools = mcp.Tools ?? new McpToolGroupOptions();
        var disabledCount = mcp.DisabledTools?.Count ?? 0;

        return string.Create(CultureInfo.InvariantCulture,
            $"Read paths: graphql=on graphql_aggregate={OnOff(options.GraphQL?.Aggregation?.Enabled ?? true)} rest_data={OnOff(options.RestApi?.DataEndpoint?.Enabled ?? true)} odata={OnOff(options.OData.Enabled)} " +
            $"arrow={OnOff(options.Arrow.Enabled)} flight_sql={OnOff(options.Arrow.Enabled && options.Arrow.EnableFlightSql)} " +
            $"duckdb_olap={OnOff(options.DuckDbOlap.Enabled)} websql={OnOff(options.WebSql.Enabled)} " +
            $"sql_endpoints={OnOff(options.SqlEndpoints.Enabled)} lakehouse={OnOff(options.Lakehouse.Enabled)} " +
            $"mcp={OnOff(mcp.Enabled)}(catalog={OnOff(tools.Catalog)},query={OnOff(tools.Query)},api={OnOff(tools.Api)},access={OnOff(tools.Access)},disabled_tools={disabledCount})");
    }

    private static string OnOff(bool on) => on ? "on" : "off";
}
