namespace Autheris.Application.Identity;

using System;
using System.Collections.Generic;
using System.Net;

/// <summary>
/// ARCH-MNT-06 (SC-01, SC-03): CIDR allowlist for SCIM callers (<c>Authentication:Realms:Scim:AllowedIpRanges</c>). An empty list is
/// unrestricted. A non-empty list admits only a peer inside one of its ranges; an entry that cannot be parsed, or a peer address that cannot
/// be evaluated (null, <see cref="IPAddress.None"/>), denies (fail closed, never silently disabled). IPv4-mapped IPv6 peers and ranges are
/// normalised to IPv4 before the comparison.
/// </summary>
public static class ScimIpAllowlist
{
    /// <summary>True when the entry is a valid CIDR range (<c>10.0.0.0/8</c>, <c>2001:db8::/32</c>) or a single address.</summary>
    public static bool IsValidRange(string? entry) => TryParseRange(entry, out _);

    /// <summary>
    /// True when the list is empty (unrestricted) or <paramref name="peer"/> lies inside one of its ranges. Returns false when any entry is
    /// invalid or the peer address cannot be evaluated.
    /// </summary>
    public static bool IsAllowed(IReadOnlyCollection<string>? ranges, IPAddress? peer)
    {
        if (ranges is null || ranges.Count == 0)
        {
            return true;
        }

        if (peer is null || peer.Equals(IPAddress.None) || peer.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        var normalizedPeer = Normalize(peer);
        var allowed = false;
        foreach (var entry in ranges)
        {
            if (!TryParseRange(entry, out var network))
            {
                return false;
            }

            allowed |= network.Contains(normalizedPeer);
        }

        return allowed;
    }

    private static bool TryParseRange(string? entry, out IPNetwork network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var text = entry.Trim();
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            // Strict CIDR: an address with host bits set (10.0.0.1/8) is a misconfiguration, never silently widened or narrowed.
            if (!IPAddress.TryParse(text[..slash], out var baseAddress)
                || !int.TryParse(text[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var prefix))
            {
                return false;
            }

            try
            {
                if (baseAddress.IsIPv4MappedToIPv6 && prefix >= 96)
                {
                    baseAddress = baseAddress.MapToIPv4();
                    prefix -= 96;
                }

                network = new IPNetwork(baseAddress, prefix);
                return network.BaseAddress.Equals(baseAddress);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        if (!IPAddress.TryParse(text, out var single))
        {
            return false;
        }

        single = Normalize(single);
        network = new IPNetwork(single, single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        return true;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
