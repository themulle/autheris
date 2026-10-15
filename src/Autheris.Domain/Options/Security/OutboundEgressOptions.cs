using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

/// <summary>
/// Outbound egress allowlist for integrations (ITSM, catalogs, OpenMetadata, lineage, lakehouse) that run inside the
/// corporate network. The SSRF guard blocks private/loopback targets by default; explicitly trusted internal hosts or
/// networks are exempt from the private-address check. Cloud metadata endpoints and link-local addresses stay blocked,
/// and HTTPS remains mandatory outside Development.
/// </summary>
public sealed class OutboundEgressOptions
{
    /// <summary>Exact host names (case-insensitive, without port), e.g. "jira.corp.local".</summary>
    public List<string> TrustedInternalHosts { get; init; } = [];

    /// <summary>CIDR networks, e.g. "10.20.0.0/16". Resolved addresses inside these networks are permitted.</summary>
    public List<string> TrustedInternalNetworks { get; init; } = [];

    /// <summary>
    /// SEC E-02: integrations that may use the trusted hosts/networks above. Null (not configured) = default
    /// <c>Itsm, Catalog, OpenMetadata, Lineage</c>. Allowed names: Itsm, Catalog, OpenMetadata, Lineage, AuditWorm, Cdn.
    /// Lakehouse can never use the allowlist (its target URLs come from Iceberg manifests, i.e. producer data).
    /// Nullable on purpose: the configuration binder appends to pre-filled lists, so a non-empty default could not be narrowed.
    /// </summary>
    public List<string>? TrustedIntegrations { get; init; }

    /// <summary>
    /// SEC I-1: when false (default) outbound integration clients never use the system/environment proxy
    /// (HTTP_PROXY/HTTPS_PROXY), so every connection is made directly to an address that passed the SSRF rules.
    /// Set to true only when the network forces egress through a proxy; connections to that proxy are then not
    /// subject to the address rules (the target URL is still validated before the request is sent).
    /// </summary>
    public bool AllowSystemProxy { get; init; }

    private static readonly string[] DefaultTrustedIntegrations = ["Itsm", "Catalog", "OpenMetadata", "Lineage"];

    /// <summary>Configured <see cref="TrustedIntegrations"/> or the default list.</summary>
    public IReadOnlyList<string> GetEffectiveTrustedIntegrations() => TrustedIntegrations ?? (IReadOnlyList<string>)DefaultTrustedIntegrations;
}

public sealed class ReverseProxyOptions
{
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Networks whose X-Forwarded-* headers are trusted. When not configured, only loopback (127.0.0.1/32, ::1/128) is
    /// trusted. A configured list replaces the default entirely. Nullable on purpose: the configuration binder appends
    /// to pre-filled lists, so a non-empty default could not be narrowed. An empty JSON array is logged
    /// as a startup warning and treated as not configured (loopback is always trusted); to trust no forwarded headers
    /// at all, set <see cref="Enabled"/> to false.
    /// </summary>
    public List<string>? KnownNetworks { get; init; }

    private static readonly string[] DefaultKnownNetworks = ["127.0.0.1/32", "::1/128"];

    /// <summary>Configured <see cref="KnownNetworks"/> or the loopback default.</summary>
    public IReadOnlyList<string> GetEffectiveKnownNetworks() => KnownNetworks ?? (IReadOnlyList<string>)DefaultKnownNetworks;

    public List<string> KnownProxies { get; init; } = [];
}

/// <summary>
/// SEC M-01: Kestrel server limits (global defaults; individual endpoints may raise the body limit explicitly).
/// </summary>
public sealed class HostingLimitsOptions
{
    [Range(1024, 1073741824)] public long MaxRequestBodySizeBytes { get; init; } = 2 * 1024 * 1024;
    [Range(1, 1000000)] public long MaxConcurrentUpgradedConnections { get; init; } = 1000;
    /// <summary>0 = unlimited (Kestrel default).</summary>
    [Range(0, 10000000)] public long MaxConcurrentConnections { get; init; } = 0;

    /// <summary>
    /// SR-L-02: accepted <c>Host</c> values (exact host, <c>host:port</c>, <c>*.suffix</c>). Required outside Development
    /// (startup fails when empty); in Development an empty list accepts loopback host names only.
    /// Also the allow-list for the CSRF Origin check.
    /// </summary>
    public List<string> AllowedHosts { get; init; } = [];
}
