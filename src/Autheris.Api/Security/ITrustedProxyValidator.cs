using System.Net;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Security;

/// <summary>
/// Singleton validator to determine whether a connection peer is a trusted ForwardAuth proxy. Only the ForwardAuth-specific
/// <c>TrustedProxies</c> and <c>TrustedNetworks</c> count (they are not merged with <c>ReverseProxy</c> lists).
/// Pre-parses and caches all IP addresses and networks at startup to avoid per-request heap allocations.
/// </summary>
public interface ITrustedProxyValidator
{
    bool IsForwardAuthPeerTrusted(IPAddress remoteIp);
}

public sealed class TrustedProxyValidator(IOptions<GatewayOptions> gatewayOptions) : ITrustedProxyValidator
{
    private readonly HashSet<IPAddress> _trustedProxies = ParseTrustedProxies(gatewayOptions?.Value ?? new GatewayOptions());
    private readonly IPNetwork[] _trustedNetworks = ParseTrustedNetworks(gatewayOptions?.Value ?? new GatewayOptions());

    private static HashSet<IPAddress> ParseTrustedProxies(GatewayOptions options)
    {
        var fwd = options.Authentication.ForwardAuth;

        var proxies = new HashSet<IPAddress>();
        foreach (var p in fwd.TrustedProxies)
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

        List<IPNetwork> networks = [];
        foreach (var net in fwd.TrustedNetworks)
        {
            if (IPNetwork.TryParse(net, out var network))
            {
                networks.Add(network);
            }
        }
        return [.. networks];
    }

    public bool IsForwardAuthPeerTrusted(IPAddress remoteIp)
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

/// <summary>
/// Raw TCP peer helpers shared by ForwardAuth and ext_authz: the peer address is captured by
/// <see cref="Autheris.Api.Middleware.CorrelationIdMiddleware"/> before <c>UseForwardedHeaders</c> rewrites
/// <c>Connection.RemoteIpAddress</c> from client-controlled <c>X-Forwarded-For</c>.
/// </summary>
public static class TrustedProxyPeer
{
    /// <summary>The raw TCP peer address (captured before forwarded headers are applied), else the connection address.</summary>
    public static IPAddress? GetRawRemoteIp(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(Autheris.Api.Middleware.CorrelationIdMiddleware.OriginalTcpRemoteIpItemKey, out var origIpObj))
        {
            if (origIpObj is IPAddress origIp)
            {
                return origIp;
            }

            if (origIpObj is string origIpStr && IPAddress.TryParse(origIpStr, out var parsedIp))
            {
                return parsedIp;
            }
        }

        return context.Connection.RemoteIpAddress;
    }
}
