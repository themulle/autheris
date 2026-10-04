namespace Autheris.Application.Security;

using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>
/// DelegatingHandler enforcing strict SSRF validation with asynchronous DNS resolution
/// across all outbound HTTP requests made by ITSM, Catalog, Lineage, Lakehouse and CDN purge clients (HIGH-03 / SEC-02).
/// Lives in Application so that both the core (Api/Infrastructure) and Autheris.Extensions can attach it (EX-12).
/// Explicitly trusted internal hosts/networks (<see cref="OutboundEgressOptions"/>) are exempt from the private-address
/// check so that on-premises integrations keep working – but only for the integration this handler instance was created
/// for, and only when that integration is listed in <see cref="OutboundEgressOptions.TrustedIntegrations"/> (SEC E-02;
/// Lakehouse and unnamed clients never). Metadata endpoints, loopback, link-local, CGNAT, multicast/broadcast and
/// "this network" targets stay blocked (SEC E-01) and HTTPS remains mandatory outside Development.
/// The connect-time check (IP pinning, no redirects) is done by the primary handler from <see cref="SecureOutboundHttp"/>.
/// </summary>
public sealed class SsrfProtectionHandler : DelegatingHandler
{
    private readonly IHostEnvironment? _environment;
    private readonly EgressAllowlist _allowlist;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolver;

    public SsrfProtectionHandler(IHostEnvironment? environment = null, IOptions<GatewayOptions>? options = null, string? integrationName = null)
        : this(environment, options, integrationName, null)
    {
    }

    internal SsrfProtectionHandler(
        IHostEnvironment? environment,
        IOptions<GatewayOptions>? options,
        string? integrationName,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver)
    {
        _environment = environment;
        _allowlist = EgressAllowlist.Create(options?.Value.Egress, integrationName);
        _resolver = resolver ?? SecureOutboundHttp.DefaultResolver;
    }

    /// <summary>Creates a handler instance for one integration client (see <see cref="SecureOutboundHttp.AddSecureOutboundHandlers"/>).</summary>
    public static SsrfProtectionHandler Create(IServiceProvider serviceProvider, string integrationName)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        return new SsrfProtectionHandler(
            serviceProvider.GetService<IHostEnvironment>(),
            serviceProvider.GetService<IOptions<GatewayOptions>>(),
            integrationName);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri != null)
        {
            bool isDev = _environment?.IsDevelopment() ?? false;
            if (!await IsTrustedInternalDestinationAsync(request.RequestUri, isDev, cancellationToken).ConfigureAwait(false))
            {
                await DeclarativeHttpDataSourceExecutor.ValidateDestinationUrlAsync(
                    request.RequestUri,
                    isDev,
                    cancellationToken).ConfigureAwait(false);

                // SEC E-01: never-allowed literals that the generic check does not cover (e.g. IPv4 multicast, 240/4).
                if (request.RequestUri.IsAbsoluteUri &&
                    (request.RequestUri.HostNameType == UriHostNameType.IPv4 || request.RequestUri.HostNameType == UriHostNameType.IPv6))
                {
                    var literal = EgressAddressRules.TryParseIpLiteral(EgressAddressRules.NormalizeHost(request.RequestUri));
                    if (literal != null && EgressAddressRules.IsAlwaysForbidden(literal))
                    {
                        throw new SecurityException($"Outbound access to restricted IP address '{EgressAddressRules.Normalize(literal)}' is strictly forbidden.");
                    }
                }
            }
        }
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns true when the destination is an explicitly trusted internal target of this integration. Throws for targets
    /// that are never allowed (metadata/loopback/link-local/CGNAT/multicast/"this network", plain HTTP outside Development)
    /// even when trusted, and for a trusted host name that does not resolve (fail-closed).
    /// </summary>
    internal async Task<bool> IsTrustedInternalDestinationAsync(Uri uri, bool isDev, CancellationToken ct)
    {
        if (_allowlist.IsEmpty || !uri.IsAbsoluteUri)
        {
            return false;
        }

        // SEC E-01: compare the Punycode host without IPv6 brackets.
        var host = EgressAddressRules.NormalizeHost(uri);
        bool hostTrusted = _allowlist.IsHostTrusted(host);

        IPAddress[] addresses;
        var directIp = uri.HostNameType == UriHostNameType.IPv4 || uri.HostNameType == UriHostNameType.IPv6
            ? EgressAddressRules.TryParseIpLiteral(host)
            : null;
        if (directIp != null)
        {
            addresses = [directIp];
        }
        else
        {
            try
            {
                addresses = await _resolver(host, ct).ConfigureAwait(false);
            }
            catch (SocketException) when (hostTrusted)
            {
                throw new SecurityException($"SSRF protection: trusted internal host '{host}' could not be resolved (fail-closed).");
            }
            catch (SocketException)
            {
                return false;
            }

            if (addresses.Length == 0)
            {
                if (hostTrusted)
                {
                    throw new SecurityException($"SSRF protection: trusted internal host '{host}' did not resolve to any address (fail-closed).");
                }

                return false;
            }
        }

        bool networkTrusted = true;
        foreach (var ip in addresses)
        {
            if (!_allowlist.IsInTrustedNetwork(ip))
            {
                networkTrusted = false;
                break;
            }
        }

        if (!hostTrusted && !networkTrusted)
        {
            return false;
        }

        if (DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost(host))
        {
            throw new SecurityException($"Outbound access to cloud/cluster metadata service '{host}' is strictly forbidden.");
        }

        foreach (var ip in addresses)
        {
            var normalized = EgressAddressRules.Normalize(ip);
            if (EgressAddressRules.IsAlwaysForbidden(normalized))
            {
                throw new SecurityException($"Outbound access to loopback/link-local/metadata/reserved address '{normalized}' is strictly forbidden, even for trusted internal hosts.");
            }
        }

        if (!isDev && !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"Insecure HTTP scheme '{uri.Scheme}' not permitted for trusted internal integrations outside Development.");
        }

        return true;
    }
}
