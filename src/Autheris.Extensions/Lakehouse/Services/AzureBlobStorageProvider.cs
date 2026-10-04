using Autheris.Application.Services;

namespace Autheris.Extensions.Lakehouse.Services;

using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Cloud storage provider for Apache Iceberg metadata and data files residing on Azure Blob Storage or Azure Data Lake Storage Gen2 (ADLS Gen2).
/// Supports Azure Storage SharedKey authorization and unsigned/anonymous blob containers.
/// </summary>
public sealed class AzureBlobStorageProvider : ILakehouseStorageProvider
{
    private readonly HttpClient? _httpClient;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<AzureBlobStorageProvider> _logger;

    private static readonly char[] InvalidContainerChars = ['/', '\\', '?', '#', '@', ':', '%'];

    private HttpClient Client => _httpClientFactory != null
        ? _httpClientFactory.CreateClient(nameof(AzureBlobStorageProvider))
        : _httpClient!;

    public AzureBlobStorageProvider(
        HttpClient httpClient,
        IOptions<GatewayOptions> options,
        ILogger<AzureBlobStorageProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public AzureBlobStorageProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<GatewayOptions> options,
        ILogger<AzureBlobStorageProvider> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }


    public async ValueTask<string> ReadTextAsync(string location, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var uri = ResolveAzureUri(location, out var account, out var container, out var blob);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyAzureAuth(request, HttpMethod.Get, uri, account, container, blob);

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Azure Blob not found: '{location}' (Resolved: '{uri}').", location);
        }

        response.EnsureSuccessStatusCode();
        // SEC: bounded read (no unbounded downloads)
        return await LakehouseLocationGuard.ReadBoundedTextAsync(
            response.Content,
            LakehouseLocationGuard.ResolveMaxReadBytes(_options.Value),
            location,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<Stream> OpenReadStreamAsync(string location, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var uri = ResolveAzureUri(location, out var account, out var container, out var blob);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyAzureAuth(request, HttpMethod.Get, uri, account, container, blob);

        var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Azure Blob not found: '{location}' (Resolved: '{uri}').", location);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> ExistsAsync(string location, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(location)) return false;

        try
        {
            var uri = ResolveAzureUri(location, out var account, out var container, out var blob);
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            ApplyAzureAuth(request, HttpMethod.Head, uri, account, container, blob);

            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check Azure Blob existence for '{Location}'", location);
            return false;
        }
    }

    public Uri ResolveAzureUri(string location, out string account, out string container, out string blob)
    {
        var opts = _options.Value.Lakehouse.Storage;
        var configuredAccount = !string.IsNullOrWhiteSpace(opts.AzureAccountName) ? opts.AzureAccountName.Trim() : "lakehouse";
        account = configuredAccount;
        container = !string.IsNullOrWhiteSpace(opts.AzureContainer) ? opts.AzureContainer : "iceberg";

        if (location.StartsWith("abfss://", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith("abfs://", StringComparison.OrdinalIgnoreCase))
        {
            // e.g. abfss://container@account.dfs.core.windows.net/path/to/blob
            var raw = location[location.IndexOf("://", StringComparison.Ordinal)..][3..];
            var atIndex = raw.IndexOf('@');
            if (atIndex > 0)
            {
                container = raw[..atIndex];
                var rest = raw[(atIndex + 1)..];
                var slashIndex = rest.IndexOf('/');
                var host = slashIndex > 0 ? rest[..slashIndex] : rest;
                blob = slashIndex > 0 ? rest[(slashIndex + 1)..] : string.Empty;

                // SEC H-18: never derive the account (and thus the signed request target) from the location.
                EnsureConfiguredAccountHost(host, configuredAccount);
            }
            else
            {
                blob = raw.TrimStart('/');
            }
        }
        else if (location.StartsWith("azure://", StringComparison.OrdinalIgnoreCase))
        {
            // e.g. azure://container/blob
            var raw = location[8..];
            var slashIndex = raw.IndexOf('/');
            if (slashIndex > 0)
            {
                container = raw[..slashIndex];
                blob = raw[(slashIndex + 1)..];
            }
            else
            {
                container = raw;
                blob = string.Empty;
            }
        }
        else if (Uri.TryCreate(location, UriKind.Absolute, out var parsedUri) && (parsedUri.Scheme == "http" || parsedUri.Scheme == "https"))
        {
            DeclarativeHttpDataSourceExecutor.ValidateUrl(parsedUri);

            // SEC H-18: only the exact configured account host ({account}.blob|dfs.core.windows.net) over https on the default port.
            if (!string.Equals(parsedUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) || !parsedUri.IsDefaultPort)
            {
                throw new System.Security.SecurityException($"SSRF protection: Azure location '{parsedUri.Host}' must use https on the default port.");
            }

            EnsureConfiguredAccountHost(parsedUri.Host, configuredAccount);

            var path = parsedUri.AbsolutePath.TrimStart('/');
            var slashIndex = path.IndexOf('/');
            container = slashIndex > 0 ? path[..slashIndex] : path;
            blob = slashIndex > 0 ? path[(slashIndex + 1)..] : string.Empty;

            LakehouseLocationGuard.EnsureSafeStorageKey(blob, location);
            EnsureValidContainer(container, location);
            return parsedUri;
        }
        else if (location.Contains("://", StringComparison.Ordinal))
        {
            throw new System.Security.SecurityException($"Unsupported Azure location scheme in '{location}'.");
        }
        else
        {
            blob = location.TrimStart('/');
        }

        LakehouseLocationGuard.EnsureSafeStorageKey(blob, location);
        EnsureValidContainer(container, location);

        var fullUriString = $"https://{configuredAccount}.blob.core.windows.net/{container}/{blob}";
        var resolvedUri = new Uri(fullUriString);
        DeclarativeHttpDataSourceExecutor.ValidateUrl(resolvedUri);

        // Defense in depth: the resolved host must still be the configured account.
        EnsureConfiguredAccountHost(resolvedUri.Host, configuredAccount);
        return resolvedUri;
    }

    /// <summary>
    /// SEC H-18: Accepts only "{account}.blob.core.windows.net" or "{account}.dfs.core.windows.net" for the configured account.
    /// </summary>
    internal static bool IsConfiguredAccountHost(string host, string configuredAccount)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(configuredAccount)) return false;

        return string.Equals(host, $"{configuredAccount}.blob.core.windows.net", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(host, $"{configuredAccount}.dfs.core.windows.net", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureConfiguredAccountHost(string host, string configuredAccount)
    {
        if (!IsConfiguredAccountHost(host, configuredAccount))
        {
            throw new System.Security.SecurityException($"SSRF protection: Outbound access to unpermitted Azure storage host '{host}' is forbidden (only the configured account is allowed).");
        }
    }

    private static void EnsureValidContainer(string container, string location)
    {
        if (string.IsNullOrWhiteSpace(container) ||
            container.IndexOfAny(InvalidContainerChars) >= 0 ||
            container == "." || container == "..")
        {
            throw new System.Security.SecurityException($"Invalid Azure container in lakehouse location '{location}'.");
        }
    }

    private void ApplyAzureAuth(HttpRequestMessage request, HttpMethod method, Uri uri, string account, string container, string blob)
    {
        var opts = _options.Value.Lakehouse.Storage;
        var now = DateTimeOffset.UtcNow;
        var rfc1123Date = now.ToString("R", CultureInfo.InvariantCulture);

        request.Headers.TryAddWithoutValidation("x-ms-date", rfc1123Date);
        request.Headers.TryAddWithoutValidation("x-ms-version", "2023-11-03");

        if (string.IsNullOrWhiteSpace(opts.AzureAccountKey))
        {
            // Unsigned/Public container or managed identity token
            return;
        }

        // SharedKey signature for Blob Service
        try
        {
            var canonicalizedHeaders = $"x-ms-date:{rfc1123Date}\nx-ms-version:2023-11-03\n";
            var canonicalizedResource = $"/{account}/{container}/{blob}".TrimEnd('/');

            var stringToSign = $"{method.Method}\n\n\n\n\n\n\n\n\n\n\n\n{canonicalizedHeaders}{canonicalizedResource}";
            var keyBytes = Convert.FromBase64String(opts.AzureAccountKey);
            using var hmac = new HMACSHA256(keyBytes);
            var sig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

            request.Headers.TryAddWithoutValidation("Authorization", $"SharedKey {account}:{sig}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to sign Azure request with SharedKey for {Uri}", uri);
        }
    }
}
