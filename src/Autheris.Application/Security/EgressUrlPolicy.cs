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

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();

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
                throw WithResolvedIp(new SecurityException("Outbound access to a restricted IP address is strictly forbidden."), directIp);
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

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
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
                // SEC I-1: fail closed; an unresolvable host must not pass validation.
                throw WithReason(new SecurityException($"SSRF protection: host '{host}' could not be resolved (fail-closed)."), "unresolved");
            }
        }

        if (addresses.Length == 0)
        {
            throw WithReason(new SecurityException($"SSRF protection: host '{host}' did not resolve to any address (fail-closed)."), "unresolved");
        }

        foreach (var ip in addresses)
        {
            var normIp = EgressAddressRules.Normalize(ip);
            if (EgressAddressRules.IsAlwaysForbidden(normIp) || EgressAddressRules.IsPrivate(normIp))
            {
                throw WithResolvedIp(new SecurityException($"Outbound access to a private/loopback/restricted address (host '{host}') is strictly forbidden."), ip);
            }
        }
    }

    /// <summary>Marks a rejection as transient DNS failure (<c>EgressReason</c>) so delivery can retry instead of dead-lettering.</summary>
    private static SecurityException WithReason(SecurityException ex, string reason)
    {
        ex.Data["EgressReason"] = reason;
        return ex;
    }

    /// <summary>SR-L-14: the address is kept out of the message (client-visible) and only attached for operator logs.</summary>
    private static SecurityException WithResolvedIp(SecurityException ex, IPAddress ip)
    {
        ex.Data["ResolvedIp"] = ip.ToString();
        return ex;
    }
}
