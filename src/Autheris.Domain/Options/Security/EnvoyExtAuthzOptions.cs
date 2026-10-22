using System.Net;

namespace Autheris.Domain.Options;

/// <summary>
/// Security review: Envoy ext_authz (<c>/api/v1/envoy/check</c>). <c>X-Original-Method</c> / <c>X-Original-URI</c>
/// describe the request being authorised and are honoured only when the raw TCP peer is listed here. This allowlist is
/// deliberately separate from the ForwardAuth / ReverseProxy trusted proxies (a reverse proxy that may set
/// <c>X-Forwarded-*</c> must not thereby be able to choose the request that ext_authz decides). Empty (default) = the
/// headers are never trusted and every check is decided on the route suffix and the real HTTP method.
/// </summary>
public sealed class EnvoyExtAuthzOptions
{
    /// <summary>Exact IP addresses of the Envoy / Istio sidecar peers allowed to send <c>X-Original-*</c> headers.</summary>
    public List<string> TrustedPeers { get; init; } = [];

    /// <summary>CIDR networks (e.g. <c>10.0.0.0/24</c>) of the Envoy peers allowed to send <c>X-Original-*</c> headers.</summary>
    public List<string> TrustedNetworks { get; init; } = [];

    /// <summary>True only when <paramref name="peer"/> is a configured trusted peer or inside a trusted network (fail closed).</summary>
    public bool IsTrustedPeer(IPAddress? peer)
    {
        if (peer == null)
        {
            return false;
        }

        var ip = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;

        foreach (var entry in TrustedPeers)
        {
            if (IPAddress.TryParse(entry, out var trusted) &&
                (trusted.IsIPv4MappedToIPv6 ? trusted.MapToIPv4() : trusted).Equals(ip))
            {
                return true;
            }
        }

        foreach (var entry in TrustedNetworks)
        {
            if (IPNetwork.TryParse(entry, out var network) && network.Contains(ip))
            {
                return true;
            }
        }

        return false;
    }
}
