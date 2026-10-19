namespace Autheris.Domain.Options.DataSources;

using System;

/// <summary>
/// Governs whether W3C TraceContext headers are propagated outbound when querying this data source.
/// </summary>
public enum OutboundPropagationMode
{
    /// <summary>Inherits the global propagation setting (default).</summary>
    Inherit = 0,

    /// <summary>Always injects W3C traceparent/tracestate headers.</summary>
    Always = 1,

    /// <summary>Strips W3C headers to prevent internal trace leakage to third-party endpoints (SEC-DS05-07).</summary>
    Never = 2
}

/// <summary>
/// Bitmask flags representing ingress transport protocols allowed to query a specific data source.
/// </summary>
[Flags]
public enum DataSourceTransportFlags
{
    None = 0,
    WebSql = 1 << 0,
    GraphQL = 1 << 1,
    Rest = 1 << 2,
    OData = 1 << 3,
    Mcp = 1 << 4,
    Trino = 1 << 5,
    ArrowFlight = 1 << 6,
    All = WebSql | GraphQL | Rest | OData | Mcp | Trino | ArrowFlight
}
