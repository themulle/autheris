namespace Autheris.Application.Security;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Autheris.Application.Services;
using Autheris.Domain.Options;

/// <summary>
/// Well-known integration names used for the per-integration egress allowlist (SEC E-02). Only integrations listed in
/// <see cref="OutboundEgressOptions.TrustedIntegrations"/> (default: Itsm, Catalog, OpenMetadata, Lineage) may use the
/// trusted internal hosts/networks. <see cref="Lakehouse"/> never may, because its target URLs come from producer data
/// (Iceberg manifests).
/// </summary>
public static class EgressIntegrations
{
    public const string Itsm = "Itsm";
    public const string Catalog = "Catalog";
    public const string OpenMetadata = "OpenMetadata";
    public const string Lineage = "Lineage";
    public const string Lakehouse = "Lakehouse";
    public const string AuditWorm = "AuditWorm";
    public const string Cdn = "Cdn";
    public const string Shadowing = "Shadowing";

    /// <summary>Integrations that may be listed in <see cref="OutboundEgressOptions.TrustedIntegrations"/>.</summary>
    public static IReadOnlyList<string> AllowlistCapable { get; } = [Itsm, Catalog, OpenMetadata, Lineage, AuditWorm, Cdn, Shadowing];
}

/// <summary>
/// SEC E-01 / E-02: compiled egress allowlist for one integration. Built from <see cref="OutboundEgressOptions"/>; empty when
/// the integration may not use the allowlist (unknown/unnamed integration, Lakehouse, or not listed in
/// <see cref="OutboundEgressOptions.TrustedIntegrations"/>). Entries that fail <see cref="Validate"/> are ignored (the
/// startup validation aborts the start for them, this is defense in depth).
/// </summary>
public sealed class EgressAllowlist
{
    private const int MinIpv4PrefixLength = 8;
    private const int MinIpv6PrefixLength = 32;

    // Networks a trusted network must neither contain nor overlap (SEC E-01). fc00::/7 (ULA) is NOT listed as a whole on
    // purpose: corporate ULA networks are legitimate on-premises targets. A network covering all of fc00::/7 is rejected
    // by the /32 minimum prefix length; the AWS reserved prefix fd00:ec2::/32 (IMDS over IPv6) is rejected explicitly.
    private static readonly IPNetwork[] ForbiddenNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("255.255.255.255/32"),
        IPNetwork.Parse("168.63.129.16/32"),
        IPNetwork.Parse("100.100.100.200/32"),
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("::/96"),
        IPNetwork.Parse("64:ff9b::/96"),
        IPNetwork.Parse("64:ff9b:1::/48"),
        IPNetwork.Parse("2002::/16"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("ff00::/8"),
        IPNetwork.Parse("fd00:ec2::/32"),
        IPNetwork.Parse("fd00:ec2::254/128")
    ];

    private readonly HashSet<string> _hosts;
    private readonly List<IPNetwork> _networks;

    private EgressAllowlist(HashSet<string> hosts, List<IPNetwork> networks)
    {
        _hosts = hosts;
        _networks = networks;
    }

    /// <summary>Allowlist without entries (private targets stay blocked).</summary>
    public static EgressAllowlist Empty { get; } = new(new HashSet<string>(StringComparer.Ordinal), []);

    public bool IsEmpty => _hosts.Count == 0 && _networks.Count == 0;

    /// <summary>True when the (normalized) host name is explicitly trusted.</summary>
    public bool IsHostTrusted(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return _hosts.Count > 0 && _hosts.Contains(EgressAddressRules.NormalizeHost(host));
    }

    /// <summary>True when the (normalized) address lies inside a trusted network.</summary>
    public bool IsInTrustedNetwork(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var ip = EgressAddressRules.Normalize(address);
        if (EgressAddressRules.IsAlwaysForbidden(ip) || EgressAddressRules.IsMetadataAddress(ip))
        {
            return false;
        }

        foreach (var network in _networks)
        {
            if (network.Contains(ip))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="integrationName"/> may use the allowlist: it must be listed in
    /// <see cref="OutboundEgressOptions.GetEffectiveTrustedIntegrations"/>; Lakehouse and unnamed clients never may.
    /// </summary>
    public static bool IsIntegrationPermitted(OutboundEgressOptions? egress, string? integrationName)
    {
        if (egress is null || string.IsNullOrWhiteSpace(integrationName) ||
            string.Equals(integrationName.Trim(), EgressIntegrations.Lakehouse, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = integrationName.Trim();
        return egress.GetEffectiveTrustedIntegrations().Any(i => string.Equals(i?.Trim(), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Builds the allowlist of one integration (empty when the integration may not use it).</summary>
    public static EgressAllowlist Create(OutboundEgressOptions? egress, string? integrationName)
    {
        if (egress is null || !IsIntegrationPermitted(egress, integrationName))
        {
            return Empty;
        }

        var hosts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in egress.TrustedInternalHosts)
        {
            if (TryNormalizeHostEntry(entry, out var host, out _))
            {
                hosts.Add(host);
            }
        }

        var networks = new List<IPNetwork>();
        foreach (var cidr in egress.TrustedInternalNetworks)
        {
            if (TryParseTrustedNetwork(cidr, out var network, out _))
            {
                networks.Add(network);
            }
        }

        return hosts.Count == 0 && networks.Count == 0 ? Empty : new EgressAllowlist(hosts, networks);
    }

    /// <summary>
    /// SEC E-01: startup validation of the egress options. Returns one message per invalid entry (empty = valid).
    /// Invalid CIDR, IPv4 prefix shorter than /8, IPv6 prefix shorter than /32, networks overlapping never-allowed ranges,
    /// host entries that are not plain host names or point to metadata/loopback targets, Lakehouse or unknown names in
    /// <see cref="OutboundEgressOptions.TrustedIntegrations"/>.
    /// </summary>
    public static IReadOnlyList<string> Validate(OutboundEgressOptions egress)
    {
        ArgumentNullException.ThrowIfNull(egress);
        var errors = new List<string>();

        foreach (var cidr in egress.TrustedInternalNetworks)
        {
            if (!TryParseTrustedNetwork(cidr, out _, out var error))
            {
                errors.Add($"Egress.TrustedInternalNetworks '{cidr}': {error}");
            }
        }

        foreach (var entry in egress.TrustedInternalHosts)
        {
            if (!TryNormalizeHostEntry(entry, out _, out var error))
            {
                errors.Add($"Egress.TrustedInternalHosts '{entry}': {error}");
            }
        }

        foreach (var integration in egress.TrustedIntegrations ?? Enumerable.Empty<string>())
        {
            var name = integration?.Trim() ?? string.Empty;
            if (string.Equals(name, EgressIntegrations.Lakehouse, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("Egress.TrustedIntegrations: 'Lakehouse' must never use the allowlist (target URLs come from producer data).");
            }
            else if (!EgressIntegrations.AllowlistCapable.Any(i => string.Equals(i, name, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Egress.TrustedIntegrations: unbekannte Integration '{integration}'. Erlaubt: {string.Join(", ", EgressIntegrations.AllowlistCapable)}.");
            }
        }

        return errors;
    }

    internal static bool TryParseTrustedNetwork(string? cidr, out IPNetwork network, out string? error)
    {
        network = default;
        var trimmed = cidr?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !trimmed.Contains('/') || !IPNetwork.TryParse(trimmed, out var parsed))
        {
            error = "invalid CIDR notation (expected e.g. 10.20.0.0/16).";
            return false;
        }

        if (parsed.BaseAddress.IsIPv4MappedToIPv6 ||
            (parsed.BaseAddress.AddressFamily == AddressFamily.InterNetworkV6 &&
             parsed.BaseAddress.GetAddressBytes().AsSpan(0, 10).SequenceEqual(stackalloc byte[10])))
        {
            error = "IPv4-mapped IPv6 networks (::ffff:0:0/96) are not allowed; use IPv4 notation.";
            return false;
        }

        bool isV4 = parsed.BaseAddress.AddressFamily == AddressFamily.InterNetwork;
        int minPrefix = isV4 ? MinIpv4PrefixLength : MinIpv6PrefixLength;
        if (parsed.PrefixLength < minPrefix)
        {
            error = $"Network too large (prefix /{parsed.PrefixLength}); at least /{minPrefix} required for {(isV4 ? "IPv4" : "IPv6")}.";
            return false;
        }

        foreach (var forbidden in ForbiddenNetworks)
        {
            if (forbidden.Contains(parsed.BaseAddress) || parsed.Contains(forbidden.BaseAddress))
            {
                error = $"Network overlaps the blocked range {forbidden} (loopback/link-local/metadata/CGNAT/multicast/IPv4-mapped).";
                return false;
            }
        }

        network = parsed;
        error = null;
        return true;
    }

    internal static bool TryNormalizeHostEntry(string? entry, out string host, out string? error)
    {
        host = string.Empty;
        var trimmed = entry?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            error = "leerer Eintrag.";
            return false;
        }

        var normalized = EgressAddressRules.NormalizeHost(trimmed);

        // SEC E-01: requests are compared by their Punycode host (Uri.IdnHost), so IDN entries are converted as well.
        if (!normalized.All(char.IsAscii))
        {
            try
            {
                normalized = new IdnMapping().GetAscii(normalized).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                error = "invalid internationalized hostname.";
                return false;
            }
        }

        var hostType = Uri.CheckHostName(normalized);
        if (hostType == UriHostNameType.Unknown || hostType == UriHostNameType.Basic)
        {
            error = "not a valid hostname (specify without scheme, port, path or wildcard).";
            return false;
        }

        if (hostType == UriHostNameType.IPv4 || hostType == UriHostNameType.IPv6)
        {
            var ip = EgressAddressRules.TryParseIpLiteral(normalized);
            if (ip is null || EgressAddressRules.IsAlwaysForbidden(ip))
            {
                error = "IP literal is blocked (loopback/link-local/metadata/CGNAT/multicast).";
                return false;
            }
        }
        else if (string.Equals(normalized, "localhost", StringComparison.Ordinal) ||
                 normalized.EndsWith(".localhost", StringComparison.Ordinal) ||
                 DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost(normalized))
        {
            error = "Metadata, cluster and loopback hosts must not be trusted.";
            return false;
        }

        host = normalized;
        error = null;
        return true;
    }
}
