using Autheris.Application.Services;

namespace Autheris.Extensions.Lakehouse.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
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
        try
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new FileNotFoundException($"Azure Blob not found: '{location}' (Resolved: '{uri}').", location);
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // EXT-03: release the connection when no stream is handed out (it leaked on 404 and error responses).
            response.Dispose();
            throw;
        }
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
        catch (Exception ex) when (ex is not InvalidOperationException and not FormatException and not SecurityException and not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to check Azure Blob existence for '{Location}'", location);
            return false;
        }
    }

    public async ValueTask<IReadOnlyList<string>> ListAsync(string prefixLocation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prefixLocation))
        {
            return Array.Empty<string>();
        }

        var baseUri = ResolveAzureUri(prefixLocation, out var account, out var container, out var blobPrefix);
        var normalizedPrefix = string.IsNullOrEmpty(blobPrefix)
            ? string.Empty
            : (blobPrefix.EndsWith("/", StringComparison.Ordinal) ? blobPrefix : blobPrefix + "/");

        var results = new List<string>();
        string? marker = null;
        const int maxPages = 20;

        for (int page = 0; page < maxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var query = $"restype=container&comp=list&prefix={Uri.EscapeDataString(normalizedPrefix)}&delimiter=%2F";
            if (!string.IsNullOrEmpty(marker))
            {
                query += $"&marker={Uri.EscapeDataString(marker)}";
            }

            var listUri = new Uri($"https://{account}.blob.core.windows.net/{container}?{query}");
            using var request = new HttpRequestMessage(HttpMethod.Get, listUri);
            ApplyAzureAuth(request, HttpMethod.Get, listUri, account, container, string.Empty);

            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                break;
            }

            response.EnsureSuccessStatusCode();
            var xml = await LakehouseLocationGuard.ReadBoundedTextAsync(
                response.Content,
                LakehouseLocationGuard.ResolveMaxReadBytes(_options.Value),
                prefixLocation,
                cancellationToken).ConfigureAwait(false);

            var (names, nextMarker) = ParseListBlobsXml(xml);
            foreach (var blobName in names)
            {
                LakehouseLocationGuard.EnsureNoTraversal(blobName, prefixLocation);
                results.Add(FormatListedLocation(prefixLocation, baseUri, account, container, blobName));
            }

            if (string.IsNullOrEmpty(nextMarker))
            {
                break;
            }

            marker = nextMarker;
        }

        return results;
    }

    private static (List<string> Names, string? NextMarker) ParseListBlobsXml(string xml)
    {
        var names = new List<string>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, settings);

        var inBlob = false;
        string? nextMarker = null;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "Blob")
                {
                    inBlob = true;
                }
                else if (inBlob && reader.Name == "Name")
                {
                    var name = reader.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name);
                    }
                }
                else if (reader.Name == "NextMarker")
                {
                    var markerVal = reader.ReadElementContentAsString().Trim();
                    nextMarker = string.IsNullOrEmpty(markerVal) ? null : markerVal;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "Blob")
            {
                inBlob = false;
            }
        }

        return (names, nextMarker);
    }

    private static string FormatListedLocation(string prefixLocation, Uri baseUri, string account, string container, string blobName)
    {
        if (prefixLocation.StartsWith("abfss://", StringComparison.OrdinalIgnoreCase))
        {
            return $"abfss://{container}@{account}.dfs.core.windows.net/{blobName}";
        }

        if (prefixLocation.StartsWith("abfs://", StringComparison.OrdinalIgnoreCase))
        {
            return $"abfs://{container}@{account}.dfs.core.windows.net/{blobName}";
        }

        if (prefixLocation.StartsWith("azure://", StringComparison.OrdinalIgnoreCase))
        {
            return $"azure://{container}/{blobName}";
        }

        if (prefixLocation.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            prefixLocation.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return $"{baseUri.Scheme}://{baseUri.Authority}/{container}/{blobName}";
        }

        return blobName;
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
            EnsureAllowedContainer(container, location);
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
        EnsureAllowedContainer(container, location);

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

    /// <summary>
    /// EXT-5: the shared key signs requests for every container of the account; only allowlisted containers may be
    /// addressed, otherwise a table location could reach another tenant's container (confused deputy).
    /// </summary>
    private void EnsureAllowedContainer(string container, string location)
    {
        if (!LakehouseLocationGuard.GetAllowedContainers(_options.Value).Contains(container))
        {
            throw new System.Security.SecurityException($"Azure container in lakehouse location '{location}' is not part of the configured lakehouse locations.");
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

        if (string.IsNullOrWhiteSpace(opts.AzureAccountKey))
        {
            if (_options.Value.AreUnsignedS3RequestsAllowed)
            {
                var rfc1123Date = now.ToString("R", CultureInfo.InvariantCulture);
                request.Headers.Remove("x-ms-date");
                request.Headers.Remove("x-ms-version");
                request.Headers.TryAddWithoutValidation("x-ms-date", rfc1123Date);
                request.Headers.TryAddWithoutValidation("x-ms-version", "2023-11-03");
                _logger.LogDebug("[INSECURE GETTING STARTED] Sending unsigned Azure Blob request to {Uri}", uri);
                return;
            }

            throw new InvalidOperationException(
                $"No Azure Storage account key provided and {nameof(GatewayOptions.AreUnsignedS3RequestsAllowed)} is false. Refusing unsigned request to '{uri}'.");
        }

        ApplySharedKeySignature(request, method, uri, account, opts.AzureAccountKey, now);
    }

    internal static void ApplySharedKeySignature(
        HttpRequestMessage request,
        HttpMethod method,
        Uri uri,
        string account,
        string accountKey,
        DateTimeOffset? utcNow = null)
    {
        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(accountKey.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Azure Storage account key is not a valid Base64 string. Refusing unsigned request.", ex);
        }

        if (keyBytes.Length == 0)
        {
            throw new InvalidOperationException("Azure Storage account key decoded to zero bytes. Refusing unsigned request.");
        }

        var now = utcNow ?? DateTimeOffset.UtcNow;
        var rfc1123Date = now.ToString("R", CultureInfo.InvariantCulture);

        request.Headers.Remove("x-ms-date");
        request.Headers.Remove("x-ms-version");
        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation("x-ms-date", rfc1123Date);
        request.Headers.TryAddWithoutValidation("x-ms-version", "2023-11-03");

        var canonicalizedHeaders = $"x-ms-date:{rfc1123Date}\nx-ms-version:2023-11-03\n";
        var canonicalizedResource = BuildCanonicalizedResource(account, uri);

        var stringToSign = $"{method.Method}\n\n\n\n\n\n\n\n\n\n\n\n{canonicalizedHeaders}{canonicalizedResource}";
        using var hmac = new HMACSHA256(keyBytes);
        var sig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

        request.Headers.TryAddWithoutValidation("Authorization", $"SharedKey {account}:{sig}");
    }

    internal static string BuildCanonicalizedResource(string account, Uri uri)
    {
        var path = uri.AbsolutePath.TrimEnd('/');
        var sb = new StringBuilder();
        sb.Append('/').Append(account).Append(path);

        if (!string.IsNullOrEmpty(uri.Query))
        {
            var rawQuery = uri.Query.StartsWith("?", StringComparison.Ordinal) ? uri.Query[1..] : uri.Query;
            if (rawQuery.Length > 0)
            {
                var queryMap = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var part in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eqIdx = part.IndexOf('=');
                    var key = eqIdx >= 0 ? part[..eqIdx] : part;
                    var val = eqIdx >= 0 ? part[(eqIdx + 1)..] : string.Empty;

                    var decodedKey = Uri.UnescapeDataString(key).ToLowerInvariant();
                    var decodedVal = Uri.UnescapeDataString(val);
                    if (!queryMap.TryGetValue(decodedKey, out var values))
                    {
                        values = [];
                        queryMap[decodedKey] = values;
                    }
                    values.Add(decodedVal);
                }

                foreach (var kvp in queryMap)
                {
                    kvp.Value.Sort(StringComparer.Ordinal);
                    sb.Append('\n').Append(kvp.Key).Append(':').Append(string.Join(',', kvp.Value));
                }
            }
        }

        return sb.ToString();
    }
}
