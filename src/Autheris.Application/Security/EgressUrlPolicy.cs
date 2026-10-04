namespace Autheris.Application.Security;

using System;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// K-E03: Unified outbound URL and destination policy across Gateway executors and plugins.
/// Enforces metadata host rejection, scheme rules, and IP reachability (always forbidden + private addresses).
/// </summary>
public static class EgressUrlPolicy
{
    public static void ValidateStatic(Uri uri, bool isDev = false, bool enforceHttps = true)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();

        if (EgressAddressRules.IsForbiddenHost(host))
        {
            throw new SecurityException($"Outbound access to cloud/cluster metadata service '{host}' is strictly forbidden.");
        }

        if (host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "169.254.169.254")
        {
            throw new SecurityException($"Outbound access to private/loopback/metadata address '{host}' is strictly forbidden.");
        }

        if (IPAddress.TryParse(host, out var directIp))
        {
            var normIp = EgressAddressRules.Normalize(directIp);
            if (EgressAddressRules.IsAlwaysForbidden(normIp) || (!isDev && EgressAddressRules.IsPrivate(normIp)))
            {
                throw new SecurityException($"Outbound access to restricted IP address '{directIp}' is strictly forbidden.");
            }
        }

        if (!isDev && enforceHttps)
        {
            if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException($"Insecure HTTP scheme '{uri.Scheme}' not permitted for outbound data sources in non-development environments.");
            }
        }
    }

    public static async Task ValidateResolvedAsync(Uri uri, bool isDev, CancellationToken ct = default)
    {
        ValidateStatic(uri, isDev);
        if (isDev)
        {
            return;
        }

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var directIp))
        {
            addresses = [directIp];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                addresses = [];
            }
        }

        foreach (var ip in addresses)
        {
            var normIp = EgressAddressRules.Normalize(ip);
            if (EgressAddressRules.IsAlwaysForbidden(normIp) || EgressAddressRules.IsPrivate(normIp))
            {
                throw new SecurityException($"Outbound access to private/loopback/restricted address '{ip}' is strictly forbidden.");
            }
        }
    }
}
