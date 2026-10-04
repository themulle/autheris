using System.Net;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Security;

/// <summary>
/// Singleton validator to determine whether incoming remote IP addresses belong to trusted reverse proxies or CIDR blocks.
/// Pre-parses and caches all IP addresses and networks at startup to avoid per-request heap allocations.
/// </summary>
public interface ITrustedProxyValidator
{
    bool IsProxyTrusted(IPAddress remoteIp);
}

public sealed class TrustedProxyValidator(IOptions<GatewayOptions> gatewayOptions) : ITrustedProxyValidator
{
    private readonly HashSet<IPAddress> _trustedProxies = ParseTrustedProxies(gatewayOptions?.Value ?? new GatewayOptions());
    private readonly IPNetwork[] _trustedNetworks = ParseTrustedNetworks(gatewayOptions?.Value ?? new GatewayOptions());

    private static HashSet<IPAddress> ParseTrustedProxies(GatewayOptions options)
    {
        var fwd = options.Authentication.ForwardAuth;
        var rp = options.ReverseProxy;

        var proxies = new HashSet<IPAddress>();
        foreach (var p in fwd.TrustedProxies.Concat(rp.KnownProxies))
        {
            if (IPAddress.TryParse(p, out var ip))
            {
                proxies.Add(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
            }
        }
        return proxies;
    }

    private static IPNetwork[] ParseTrustedNetworks(GatewayOptions options)
    {
        var fwd = options.Authentication.ForwardAuth;
        var rp = options.ReverseProxy;

        List<IPNetwork> networks = [];
        foreach (var net in fwd.TrustedNetworks.Concat(rp.KnownNetworks))
        {
            if (IPNetwork.TryParse(net, out var network))
            {
                networks.Add(network);
            }
        }
        return [.. networks];
    }

    public bool IsProxyTrusted(IPAddress remoteIp)
    {
        ArgumentNullException.ThrowIfNull(remoteIp);

        var ip = remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp;

        if (_trustedProxies.Contains(ip))
        {
            return true;
        }

        foreach (var net in _trustedNetworks)
        {
            if (net.Contains(ip))
            {
                return true;
            }
        }

        return false;
    }
}
