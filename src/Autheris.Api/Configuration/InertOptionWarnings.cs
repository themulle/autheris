using System.Collections.Generic;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

namespace Autheris.Api.Configuration;

/// <summary>
/// Settings that are accepted at startup but have no effect on some read paths. Each one is reported once as a startup
/// warning, so an operator does not assume a setting is active where it is not.
/// </summary>
public static class InertOptionWarnings
{
    /// <summary>Returns one message per configured setting that is partly or fully inert.</summary>
    public static IReadOnlyList<string> Collect(GatewayOptions options)
    {
        var warnings = new List<string>();

        // The governed SQL compiler always emits correlated policy subqueries (GovernedSubqueryStrategy.Correlated); only the
        // text row-filter builders (RowFilterSqlBuilder, RlsFilterGenerator) read this setting.
        if (options.RowFilters.SubqueryStrategy != RowFilterSubqueryStrategy.Exists)
        {
            warnings.Add(
                $"Gateway:RowFilters:SubqueryStrategy = {options.RowFilters.SubqueryStrategy} has no effect on the governed SQL compiler " +
                "(WebSQL, Trino protocol, Arrow/Flight SQL, async jobs, SQL endpoints, MCP SQL tools), which always uses correlated " +
                "policy subqueries. It applies only to row filters built as SQL text (REST data API, OLAP and federated staging, " +
                "governed aggregations, procedures, change detection).");
        }

        return warnings;
    }
}
