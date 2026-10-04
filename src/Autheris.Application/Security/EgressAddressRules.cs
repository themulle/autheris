namespace Autheris.Application.Security;

using System;
using System.Net;
using System.Net.Sockets;
using Autheris.Application.Services;

/// <summary>
/// SEC E-01 / E-03: address rules shared by the SSRF handler and the connect-time check of the hardened outbound
/// primary handler (<see cref="SecureOutboundHttp"/>).
/// <list type="bullet">
/// <item><see cref="IsAlwaysForbidden"/>: never reachable, not even for trusted internal integrations or in Development
/// (this-network 0.0.0.0/8 and "::", loopback, link-local, CGNAT 100.64.0.0/10, multicast, broadcast/reserved
/// 240.0.0.0/4, IPv4-compatible IPv6, the AWS reserved IPv6 prefix fd00:ec2::/32 and the cloud metadata endpoints).</item>
/// <item><see cref="IsPrivate"/>: private/internal (RFC 1918, ULA fc00::/7, ...); permitted only for trusted internal
/// targets of integrations that may use the egress allowlist, and in Development.</item>
/// </list>
/// IPv4-mapped IPv6 addresses (::ffff:a.b.c.d) are normalized to IPv4 before every check.
/// </summary>
public static class EgressAddressRules
{
    /// <summary>
    /// Cloud metadata / platform endpoints (AWS IMDS v4, ECS task metadata, Alibaba IMDS, AWS IMDS over IPv6,
    /// Azure WireServer). Most of them are also covered by the range rules; 168.63.129.16 is a public address.
    /// </summary>
    private static readonly IPAddress[] MetadataAddresses =
    [
        IPAddress.Parse("169.254.169.254"),
        IPAddress.Parse("169.254.170.2"),
        IPAddress.Parse("100.100.100.200"),
        IPAddress.Parse("fd00:ec2::254"),
        IPAddress.Parse("168.63.129.16")
    ];

    /// <summary>Normalizes IPv4-mapped IPv6 addresses to IPv4.</summary>
    public static IPAddress Normalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    /// <summary>True for metadata endpoints (see class remarks), after normalization.</summary>
    public static bool IsMetadataAddress(IPAddress address)
    {
        var ip = Normalize(address);
        foreach (var metadata in MetadataAddresses)
        {
            if (metadata.Equals(ip))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True for addresses that must never be contacted by outbound integrations, regardless of allowlist or environment.
    /// </summary>
    public static bool IsAlwaysForbidden(IPAddress address)
    {
        var ip = Normalize(address);
        if (IsMetadataAddress(ip) || IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0 ||                                  // 0.0.0.0/8 ("this network", 0.0.0.0 = localhost on Linux)
                   b[0] == 127 ||                                // 127.0.0.0/8 loopback
                   (b[0] == 169 && b[1] == 254) ||               // 169.254.0.0/16 link-local (IMDS)
                   (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || // 100.64.0.0/10 CGNAT (Alibaba IMDS 100.100.100.200)
                   b[0] >= 224;                                  // 224.0.0.0/4 multicast, 240.0.0.0/4 reserved, 255.255.255.255 broadcast
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast)
            {
                return true;
            }

            var b = ip.GetAddressBytes();

            // fd00:ec2::/32 – AWS reserved IPv6 prefix (IMDS fd00:ec2::254, DNS, NTP). The rest of fc00::/7 (ULA) is private,
            // not forbidden, so that legitimate corporate ULA networks can be trusted explicitly.
            if (b[0] == 0xFD && b[1] == 0x00 && b[2] == 0x0E && b[3] == 0xC2)
            {
                return true;
            }

            bool firstTwelveZero = true;
            for (int i = 0; i < 12; i++)
            {
                if (b[i] != 0)
                {
                    firstTwelveZero = false;
                    break;
                }
            }

            // ::/96 – deprecated IPv4-compatible IPv6 addresses (includes :: and ::1).
            if (firstTwelveZero)
            {
                return true;
            }

            // 64:ff9b::/96 – NAT64 well-known prefix: check the embedded IPv4 address.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B &&
                b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
            {
                var embedded = new IPAddress(new[] { b[12], b[13], b[14], b[15] });
                return IsAlwaysForbidden(embedded) || IsPrivate(embedded);
            }

            // 64:ff9b:1::/48 – NAT64 local prefix: check the embedded IPv4 address.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4] == 0x00 && b[5] == 0x01)
            {
                var embedded = new IPAddress(new[] { b[12], b[13], b[14], b[15] });
                return IsAlwaysForbidden(embedded) || IsPrivate(embedded);
            }

            // 2002::/16 – 6to4 prefix: bytes 2..5 contain embedded IPv4 address.
            if (b[0] == 0x20 && b[1] == 0x02)
            {
                var embedded = new IPAddress(new[] { b[2], b[3], b[4], b[5] });
                return IsAlwaysForbidden(embedded) || IsPrivate(embedded);
            }
        }

        return false;
    }

    /// <summary>
    /// True for forbidden cloud metadata and cluster internal service hosts.
    /// </summary>
    public static bool IsForbiddenHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var h = NormalizeHost(host);
        return h == "metadata.google.internal" ||
               h.EndsWith(".metadata.google.internal", StringComparison.OrdinalIgnoreCase) ||
               h == "kubernetes.default.svc" ||
               h.EndsWith(".kubernetes.default.svc", StringComparison.OrdinalIgnoreCase) ||
               h.StartsWith("kubernetes.default.svc.", StringComparison.OrdinalIgnoreCase) ||
               h == "localhost" ||
               h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for private/internal addresses (RFC 1918, ULA, site-local, ...).
    /// </summary>
    public static bool IsPrivate(IPAddress address)
    {
        var ip = Normalize(address);
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            // RFC 1918: 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // RFC 1918: 172.16.0.0/12 (172.16.0.0 - 172.31.255.255)
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // RFC 1918: 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            // Unique Local Address (ULA) fc00::/7 (RFC 4193: fc00:: to fdff::)
            if ((bytes[0] & 0xFE) == 0xFC) return true;
            if (ip.IsIPv6SiteLocal) return true;
            return false;
        }

        return false;
    }

    /// <summary>
    /// Connect-time decision for one candidate address: never-allowed addresses are rejected everywhere; in Development
    /// everything else is permitted (same as the SSRF handler, whose private-address rule only applies outside
    /// Development); otherwise private addresses are permitted only for an explicitly trusted host name or inside a trusted
    /// network of an integration that may use the allowlist.
    /// </summary>
    public static bool IsAddressPermitted(IPAddress address, bool isDevelopment, bool hostTrusted, EgressAllowlist allowlist)
    {
        ArgumentNullException.ThrowIfNull(allowlist);
        var ip = Normalize(address);
        if (IsAlwaysForbidden(ip))
        {
            return false;
        }

        if (isDevelopment || !IsPrivate(ip))
        {
            return true;
        }

        return hostTrusted || allowlist.IsInTrustedNetwork(ip);
    }

    /// <summary>
    /// Normalizes a host name for comparisons: removes IPv6 brackets and a trailing dot, lower-case invariant.
    /// </summary>
    public static string NormalizeHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var h = host.Trim();
        if (h.Length >= 2 && h.StartsWith('[') && h.EndsWith(']'))
        {
            h = h[1..^1];
        }

        return h.TrimEnd('.').ToLowerInvariant();
    }

    /// <summary>
    /// Host of a URI for comparisons and resolution: Punycode (<see cref="Uri.IdnHost"/>), without IPv6 brackets.
    /// </summary>
    public static string NormalizeHost(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return NormalizeHost(uri.IdnHost);
    }

    /// <summary>
    /// Parses an IP literal host (IPv4 or IPv6, with or without brackets / zone id). Returns null for DNS names.
    /// </summary>
    public static IPAddress? TryParseIpLiteral(string normalizedHost)
    {
        ArgumentNullException.ThrowIfNull(normalizedHost);
        var hostType = Uri.CheckHostName(normalizedHost);
        if (hostType != UriHostNameType.IPv4 && hostType != UriHostNameType.IPv6)
        {
            return null;
        }

        return IPAddress.TryParse(normalizedHost, out var ip) ? ip : null;
    }
}
