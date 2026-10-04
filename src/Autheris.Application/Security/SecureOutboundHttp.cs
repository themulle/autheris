namespace Autheris.Application.Security;

using System;
using System.Collections.Generic;
using System.IO;
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
/// SEC E-03 / E-04: hardened outbound HTTP stack for all integration clients (ITSM, catalogs, OpenMetadata, OpenLineage,
/// OpenJEV, lakehouse storage, audit WORM export, CDN purge).
/// <list type="bullet">
/// <item>Primary handler is a <see cref="SocketsHttpHandler"/> with <c>AllowAutoRedirect = false</c>: redirects are never
/// followed, a 3xx response is returned to the client and treated as an error there (none of the integrations relies on
/// redirects). This also prevents custom credential headers (e.g. Alation <c>TOKEN</c>) from reaching foreign origins.</item>
/// <item>Its <c>ConnectCallback</c> resolves the host itself, applies the same decision as <see cref="SsrfProtectionHandler"/>
/// (integration allowlist + never-allowed ranges + private-address rule outside Development) to EVERY candidate address and
/// connects only to a checked address (IP pinning, no second DNS lookup, defeats DNS rebinding).</item>
/// <item>Connections to the system proxy (<see cref="HttpClient.DefaultProxy"/>, e.g. HTTPS_PROXY) are not subject to the
/// address rules, because the proxy endpoint is operator configuration; the target URL is still checked by the
/// <see cref="SsrfProtectionHandler"/> before the request is sent.</item>
/// </list>
/// </summary>
public static class SecureOutboundHttp
{
    /// <summary>Default DNS resolver (overridable in tests).</summary>
    internal static Task<IPAddress[]> DefaultResolver(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);

    /// <summary>
    /// Registers the hardened primary handler and the <see cref="SsrfProtectionHandler"/> for one integration client.
    /// </summary>
    public static IHttpClientBuilder AddSecureOutboundHandlers(this IHttpClientBuilder builder, string integrationName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(integrationName);

        return builder
            .ConfigurePrimaryHttpMessageHandler(sp => CreatePrimaryHandler(sp, integrationName))
            .AddHttpMessageHandler(sp => SsrfProtectionHandler.Create(sp, integrationName));
    }

    /// <summary>
    /// Creates the hardened primary handler for <paramref name="integrationName"/> using the gateway options and host
    /// environment from <paramref name="serviceProvider"/> (missing environment = treated as non-Development).
    /// </summary>
    public static SocketsHttpHandler CreatePrimaryHandler(IServiceProvider serviceProvider, string integrationName)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        var environment = serviceProvider.GetService<IHostEnvironment>();
        var options = serviceProvider.GetService<IOptions<GatewayOptions>>()?.Value ?? new GatewayOptions();
        bool isDev = environment?.IsDevelopment() ?? false;

        return CreatePrimaryHandler(
            isDev,
            EgressAllowlist.Create(options.Egress, integrationName),
            options.AreUntrustedCertificatesAllowed,
            DefaultResolver);
    }

    internal static SocketsHttpHandler CreatePrimaryHandler(
        bool isDevelopment,
        EgressAllowlist allowlist,
        bool allowUntrustedCertificates,
        Func<string, CancellationToken, Task<IPAddress[]>> resolver)
    {
        ArgumentNullException.ThrowIfNull(allowlist);
        ArgumentNullException.ThrowIfNull(resolver);

#pragma warning disable CA5359 // Only with danger_allow_untrusted_certificates (DANGER, Development only; same as the HttpClient defaults)
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions = allowUntrustedCertificates
                ? new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = delegate { return true; }
                }
                : new System.Net.Security.SslClientAuthenticationOptions(),
            ConnectCallback = (context, cancellationToken) =>
                ConnectAsync(context.DnsEndPoint, context.InitialRequestMessage.RequestUri, isDevelopment, allowlist, resolver, cancellationToken)
        };
#pragma warning restore CA5359
    }

    /// <summary>
    /// Connect-time decision (testable): validates every candidate address of <paramref name="host"/> and returns the
    /// normalized addresses that may be connected to. Throws <see cref="SecurityException"/> when no address was resolved
    /// (fail-closed) or when any candidate is not permitted (a mixed answer is treated as a rebinding attempt).
    /// </summary>
    public static IReadOnlyList<IPAddress> SelectPermittedAddresses(
        string host,
        IReadOnlyList<IPAddress> candidates,
        bool isDevelopment,
        EgressAllowlist allowlist)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(allowlist);

        var normalizedHost = EgressAddressRules.NormalizeHost(host);
        if (DeclarativeHttpDataSourceExecutor.IsForbiddenMetadataHost(normalizedHost))
        {
            throw new SecurityException($"Outbound access to cloud/cluster metadata service '{normalizedHost}' is strictly forbidden.");
        }

        if (candidates.Count == 0)
        {
            throw new SecurityException($"SSRF protection: host '{normalizedHost}' did not resolve to any address (fail-closed).");
        }

        bool hostTrusted = allowlist.IsHostTrusted(normalizedHost);
        var permitted = new List<IPAddress>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var ip = EgressAddressRules.Normalize(candidate);
            if (!EgressAddressRules.IsAddressPermitted(ip, isDevelopment, hostTrusted, allowlist))
            {
                throw new SecurityException($"SSRF / DNS Rebinding Defense: Outbound connection to restricted IP address '{ip}' is strictly forbidden.");
            }

            permitted.Add(ip);
        }

        return permitted;
    }

    /// <summary>Resolves <paramref name="host"/> (IP literals without DNS) and applies <see cref="SelectPermittedAddresses"/>.</summary>
    internal static async Task<IReadOnlyList<IPAddress>> ResolvePermittedAddressesAsync(
        string host,
        bool isDevelopment,
        EgressAllowlist allowlist,
        Func<string, CancellationToken, Task<IPAddress[]>> resolver,
        CancellationToken cancellationToken)
    {
        var normalizedHost = EgressAddressRules.NormalizeHost(host);
        var literal = EgressAddressRules.TryParseIpLiteral(normalizedHost);
        IPAddress[] candidates = literal is not null
            ? [literal]
            : await resolver(normalizedHost, cancellationToken).ConfigureAwait(false);

        return SelectPermittedAddresses(normalizedHost, candidates, isDevelopment, allowlist);
    }

    private static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint endPoint,
        Uri? requestUri,
        bool isDevelopment,
        EgressAllowlist allowlist,
        Func<string, CancellationToken, Task<IPAddress[]>> resolver,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IPAddress> addresses;
        if (IsSystemProxyEndpoint(endPoint, requestUri))
        {
            var proxyHost = EgressAddressRules.NormalizeHost(endPoint.Host);
            var literal = EgressAddressRules.TryParseIpLiteral(proxyHost);
            addresses = literal is not null
                ? [literal]
                : await resolver(proxyHost, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            addresses = await ResolvePermittedAddressesAsync(endPoint.Host, isDevelopment, allowlist, resolver, cancellationToken).ConfigureAwait(false);
        }

        Exception? lastError = null;
        foreach (var ip in addresses)
        {
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, endPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                socket.Dispose();
                lastError = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw lastError ?? new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>
    /// True when the connection target is the system proxy for <paramref name="requestUri"/> (not the request host itself).
    /// </summary>
    internal static bool IsSystemProxyEndpoint(DnsEndPoint endPoint, Uri? requestUri)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        if (requestUri is null || !requestUri.IsAbsoluteUri)
        {
            return false;
        }

        var endpointHost = EgressAddressRules.NormalizeHost(endPoint.Host);
        if (string.Equals(endpointHost, EgressAddressRules.NormalizeHost(requestUri), StringComparison.Ordinal))
        {
            return false;
        }

        var proxy = HttpClient.DefaultProxy;
        if (proxy.IsBypassed(requestUri))
        {
            return false;
        }

        var proxyUri = proxy.GetProxy(requestUri);
        return proxyUri is not null &&
               proxyUri.IsAbsoluteUri &&
               proxyUri.Port == endPoint.Port &&
               string.Equals(EgressAddressRules.NormalizeHost(proxyUri), endpointHost, StringComparison.Ordinal);
    }
}
