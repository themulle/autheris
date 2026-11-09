namespace Autheris.Application.DataSources;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Options.DataSources;

/// <summary>Issue #447: the individual transport names behind a flag value, for admin diagnostics and reports.</summary>
public static class TransportFlagNames
{
    private static readonly DataSourceTransportFlags[] Singles =
    [
        DataSourceTransportFlags.WebSql, DataSourceTransportFlags.GraphQL, DataSourceTransportFlags.Rest, DataSourceTransportFlags.OData,
        DataSourceTransportFlags.Mcp, DataSourceTransportFlags.Trino
    ];

    /// <summary>The names of the single flags set in <paramref name="flags"/> (empty for <c>None</c>); never the aggregate <c>All</c>.</summary>
    public static IReadOnlyList<string> Of(DataSourceTransportFlags flags) =>
        Singles.Where(f => (flags & f) == f).Select(f => f.ToString()).ToList();
}
