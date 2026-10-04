using System.Net;
using System.Net.Sockets;

namespace Autheris.Infrastructure.RateLimiting;

/// <summary>
/// SEC M-08: Builds the pre-auth rate-limit key for a client IP.
/// IPv6 clients usually control a whole /64 (SLAAC / privacy extensions), so IPv6 addresses are aggregated
/// to their /64 prefix. IPv4-mapped IPv6 addresses are normalized to plain IPv4.
/// </summary>
public static class ClientIpRateLimitKey
{
    public static string Normalize(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return "unknown";
        }

        var trimmed = ip.Trim();
        if (!IPAddress.TryParse(trimmed, out var address))
        {
            return trimmed.Length > 64 ? trimmed[..64] : trimmed;
        }

        return Normalize(address);
    }

    public static string Normalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        for (var i = 8; i < bytes.Length; i++)
        {
            bytes[i] = 0;
        }

        return new IPAddress(bytes).ToString() + "/64";
    }
}
