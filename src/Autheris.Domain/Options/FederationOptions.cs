namespace Autheris.Domain.Options;

using System;
using System.Collections.Generic;

/// <summary>
/// Configuration for Federated Subgraphs & Hot Chocolate Fusion execution (P7).
/// </summary>
public sealed class FederationOptions
{
    /// <summary>
    /// Enables Hot Chocolate Fusion federated GraphQL gateway execution.
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Path to the compiled Fusion Gateway Package file (.fgp) containing the composed supergraph.
    /// </summary>
    public string GatewayPackagePath { get; init; } = string.Empty;

    /// <summary>
    /// Enables Zero-Trust context forwarding (User SID, Tenant ID, Roles) to downstream subgraphs.
    /// </summary>
    public bool EnableZeroTrustContextForwarding { get; init; } = true;

    /// <summary>
    /// Enables in-memory PII/sensitive column masking on aggregated subgraph response results.
    /// </summary>
    public bool EnableResultMasking { get; init; } = true;

    /// <summary>
    /// Header name used to forward the authenticated caller SID or user identifier.
    /// </summary>
    public string SubjectHeaderName { get; init; } = "X-Gateway-Subject";

    /// <summary>
    /// Header name used to forward tenant isolation context.
    /// </summary>
    public string TenantHeaderName { get; init; } = "X-Tenant-ID";

    /// <summary>
    /// Header name used to forward caller roles.
    /// </summary>
    public string RolesHeaderName { get; init; } = "X-Gateway-Roles";

    /// <summary>
    /// Forward the original Authorization Bearer token downstream to subgraphs if present.
    /// </summary>
    public bool ForwardAuthorizationBearer { get; init; } = true;

    /// <summary>
    /// List of registered subgraph endpoints.
    /// </summary>
    public List<SubgraphEndpointOptions> Subgraphs { get; init; } = [];
}

/// <summary>
/// Configuration for a specific federated subgraph.
/// </summary>
public sealed class SubgraphEndpointOptions
{
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 30;
}
