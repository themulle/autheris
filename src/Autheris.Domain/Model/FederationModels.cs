namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// Metadata descriptor for a federated GraphQL Subgraph endpoint (Apollo Federation v2 or Hot Chocolate Fusion).
/// </summary>
public sealed record SubgraphEndpointInfo(
    string Name,
    Uri Url,
    int TimeoutSeconds = 30
);

/// <summary>
/// Security context propagated downstream to federated subgraphs across the Zero-Trust mesh.
/// </summary>
public sealed record SubgraphSecurityContext(
    string? SubjectSid,
    string? TenantId,
    IReadOnlyList<string> Roles,
    string? CorrelationId
);
