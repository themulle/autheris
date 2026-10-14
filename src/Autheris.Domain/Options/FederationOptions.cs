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
    /// Cryptographic signing secret used for HMAC-SHA256 signing of forwarded Zero-Trust context headers (1.10).
    /// </summary>
    public string? SigningKey { get; init; }

    /// <summary>
    /// When enabled, context headers forwarded to downstream subgraphs are signed with HMAC-SHA256 to prevent header spoofing (1.10).
    /// </summary>
    public bool SignContextHeaders { get; init; } = true;

    /// <summary>
    /// SR-M-18: when true, subgraph-side verifiers built on <c>SubgraphSecurityValidator</c> also accept the legacy
    /// (unversioned, body-less) signature format to allow rolling upgrades. Default false (fail closed, v2 only).
    /// </summary>
    public bool AllowLegacyContextSignature { get; init; }

    /// <summary>
    /// Enables in-memory PII/sensitive column masking on aggregated subgraph response results.
    /// </summary>
    public bool EnableResultMasking { get; init; } = true;

    /// <summary>
    /// SR-L-12: explicit field classification overrides for federated results. Keys are <c>subgraph.type.field</c>
    /// (also <c>type.field</c> or just <c>field</c>; result trees carry no type information, so only the field segment is matched,
    /// case- and underscore-insensitively). Values are masking rule types (<c>REDACT</c>, <c>MASK_EMAIL</c>, <c>MASK_PHONE</c>,
    /// <c>MASK_IBAN</c>, <c>NULLIFY</c>, ...) or <c>NONE</c> to exempt a field. Entries take precedence over catalog and name heuristic.
    /// </summary>
    public Dictionary<string, string> FieldClassifications { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SR-L-12: when the catalog classification cannot be loaded, mask all scalar result values except identifiers
    /// (<c>id</c>, <c>*Id</c>, <c>__typename</c>) instead of relying on the name heuristic alone (fail closed).
    /// Default false for compatibility; recommended true for new installations.
    /// </summary>
    public bool DefaultMaskUnknownSensitiveScalars { get; init; }

    /// <summary>
    /// SR-L-12: lifetime in seconds of the per-tenant catalog classification cache used for federated masking.
    /// </summary>
    public int CatalogMaskingCacheSeconds { get; init; } = 60;

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
    /// Defaults to false (least privilege / token leakage prevention).
    /// </summary>
    public bool ForwardAuthorizationBearer { get; init; } = false;

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

    /// <summary>
    /// Explicit opt-in to forward the client's raw Authorization Bearer token to this specific subgraph.
    /// Defaults to false. Never forwarded for MCP agent requests to prevent token exfiltration.
    /// </summary>
    public bool ForwardClientBearerToken { get; init; } = false;
}
