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
/// Cloud storage provider for Apache Iceberg metadata and data files residing on AWS S3 or MinIO.
/// Supports zero-trust SigV4 request signing and unsigned requests for local test rigs.
/// </summary>
public sealed class S3LakehouseStorageProvider : ILakehouseStorageProvider
{
    private readonly HttpClient? _httpClient;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<S3LakehouseStorageProvider> _logger;

    private static readonly System.Text.RegularExpressions.Regex S3BucketNameRegex = new(
        "^[a-z0-9][a-z0-9.\\-]{1,61}[a-z0-9]$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private HttpClient Client => _httpClientFactory != null
        ? _httpClientFactory.CreateClient(nameof(S3LakehouseStorageProvider))
        : _httpClient!;

    public S3LakehouseStorageProvider(
        HttpClient httpClient,
        IOptions<GatewayOptions> options,
        ILogger<S3LakehouseStorageProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public S3LakehouseStorageProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<GatewayOptions> options,
        ILogger<S3LakehouseStorageProvider> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }


    public async ValueTask<string> ReadTextAsync(string location, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var uri = ResolveS3Uri(location, out var bucket, out var key);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplySigV4OrUnsigned(request, HttpMethod.Get, uri, bucket, key);

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"S3 object not found: '{location}' (Resolved: '{uri}').", location);
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
        var uri = ResolveS3Uri(location, out var bucket, out var key);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplySigV4OrUnsigned(request, HttpMethod.Get, uri, bucket, key);

        var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"S3 object not found: '{location}' (Resolved: '{uri}').", location);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> ExistsAsync(string location, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(location)) return false;

        try
        {
            var uri = ResolveS3Uri(location, out var bucket, out var key);
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            ApplySigV4OrUnsigned(request, HttpMethod.Head, uri, bucket, key);

            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check S3 object existence for '{Location}'", location);
            return false;
        }
    }

    public Uri ResolveS3Uri(string location, out string bucket, out string key)
    {
        var s3Opts = _options.Value.Lakehouse.Storage;

        if (location.StartsWith("s3://", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith("minio://", StringComparison.OrdinalIgnoreCase))
        {
            var raw = location[location.IndexOf("://", StringComparison.Ordinal)..][3..];
            var slashIndex = raw.IndexOf('/');
            if (slashIndex < 0)
            {
                bucket = raw;
                key = string.Empty;
            }
            else
            {
                bucket = raw[..slashIndex];
                key = raw[(slashIndex + 1)..];
            }
        }
        else if (Uri.TryCreate(location, UriKind.Absolute, out var parsedUri) && (parsedUri.Scheme == "http" || parsedUri.Scheme == "https"))
        {
            DeclarativeHttpDataSourceExecutor.ValidateUrl(parsedUri);
            LakehouseLocationGuard.EnsureNoTraversal(parsedUri.OriginalString, location);

            var path = parsedUri.AbsolutePath.TrimStart('/');
            var pathSlash = path.IndexOf('/');
            var pathBucket = pathSlash >= 0 ? path[..pathSlash] : path;
            var pathKey = pathSlash >= 0 ? path[(pathSlash + 1)..] : string.Empty;

            var allowed = false;
            string derivedBucket = pathBucket;
            string derivedKey = pathKey;

            if (!string.IsNullOrWhiteSpace(s3Opts.S3Endpoint) && Uri.TryCreate(s3Opts.S3Endpoint, UriKind.Absolute, out var endpointUri))
            {
                // SEC M-33: exact configured endpoint (host + port + scheme), path-style addressing
                allowed = string.Equals(parsedUri.Host, endpointUri.Host, StringComparison.OrdinalIgnoreCase) &&
                          parsedUri.Port == endpointUri.Port &&
                          string.Equals(parsedUri.Scheme, endpointUri.Scheme, StringComparison.OrdinalIgnoreCase);
            }
            else if (IsAmazonS3Host(parsedUri.Host) &&
                     string.Equals(parsedUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
                     parsedUri.IsDefaultPort)
            {
                allowed = true;
                // Virtual-hosted style: {bucket}.s3[.-]{region}.amazonaws.com
                var s3Index = parsedUri.Host.IndexOf(".s3", StringComparison.OrdinalIgnoreCase);
                if (s3Index > 0)
                {
                    derivedBucket = parsedUri.Host[..s3Index];
                    derivedKey = path;
                }
            }

            if (!allowed)
            {
                throw new System.Security.SecurityException($"SSRF protection: Outbound access to unpermitted S3 location host '{parsedUri.Host}' is forbidden.");
            }

            bucket = derivedBucket;
            key = derivedKey;
            EnsureBucketAndKeyAllowed(bucket, key, location);
            return parsedUri;
        }
        else if (location.Contains("://", StringComparison.Ordinal))
        {
            throw new System.Security.SecurityException($"Unsupported S3 location scheme in '{location}'.");
        }
        else
        {
            bucket = !string.IsNullOrWhiteSpace(s3Opts.S3Bucket) ? s3Opts.S3Bucket : "lakehouse";
            key = location.TrimStart('/');
        }

        EnsureBucketAndKeyAllowed(bucket, key, location);

        var endpoint = !string.IsNullOrWhiteSpace(s3Opts.S3Endpoint)
            ? s3Opts.S3Endpoint.TrimEnd('/')
            : "https://s3.amazonaws.com";

        // Path-style URI (compatible with MinIO, Ceph, LocalStack, and AWS)
        var fullUriString = $"{endpoint}/{bucket}/{key}";
        var resolvedUri = new Uri(fullUriString);
        DeclarativeHttpDataSourceExecutor.ValidateUrl(resolvedUri);
        return resolvedUri;
    }

    /// <summary>
    /// SEC M-33 / E-02 / EX-12: without a configured S3Endpoint only genuine S3 endpoints are accepted:
    /// s3.amazonaws.com, s3.&lt;region&gt;, s3-&lt;region&gt;, s3-accelerate, s3.dualstack.&lt;region&gt;, s3-fips, s3-website,
    /// each optionally prefixed with a (virtual-hosted) bucket. Other AWS service hosts (ELB, EC2, execute-api, ...) resolve
    /// to internal addresses inside a VPC and are rejected, even when their name contains an "s3" label.
    /// </summary>
    internal static bool IsAmazonS3Host(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        return AmazonS3HostRegex.IsMatch(host.TrimEnd('.'));
    }

    private static readonly System.Text.RegularExpressions.Regex AmazonS3HostRegex = new(
        "^(?:[a-z0-9][a-z0-9.-]*\\.)?s3(?:[.-](?:accelerate|dualstack|fips|external-1|website|[a-z]{2}(?:-[a-z]+)+-[0-9]+))*\\.amazonaws\\.com$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));

    private void EnsureBucketAndKeyAllowed(string bucket, string key, string location)
    {
        if (!S3BucketNameRegex.IsMatch(bucket))
        {
            throw new System.Security.SecurityException($"Invalid S3 bucket name in lakehouse location '{location}'.");
        }

        // SEC EX-10 / M-33: reject ?, #, % and traversal segments in object keys
        LakehouseLocationGuard.EnsureSafeStorageKey(key, location);

        // SEC M-33: bucket allowlist (configured S3 bucket + buckets of configured table locations)
        var allowedBuckets = LakehouseLocationGuard.GetAllowedBuckets(_options.Value);
        if (allowedBuckets.Count > 0 && !allowedBuckets.Contains(bucket))
        {
            throw new System.Security.SecurityException($"S3 bucket '{bucket}' is not part of the configured lakehouse locations.");
        }
    }

    private void ApplySigV4OrUnsigned(HttpRequestMessage request, HttpMethod method, Uri uri, string bucket, string key)
    {
        var s3Opts = _options.Value.Lakehouse.Storage;
        var accessKey = s3Opts.S3AccessKey;
        var secretKey = s3Opts.S3SecretKey;

        if (string.IsNullOrWhiteSpace(accessKey) || string.IsNullOrWhiteSpace(secretKey))
        {
            if (_options.Value.AreUnsignedS3RequestsAllowed)
            {
                _logger.LogDebug("[INSECURE GETTING STARTED] Sending unsigned S3 request to {Uri}", uri);
                return;
            }

            throw new InvalidOperationException(
                $"No S3 credentials provided and {nameof(GatewayOptions.AreUnsignedS3RequestsAllowed)} is false. Refusing unsigned request to '{uri}'.");
        }

        SignAwsSigV4(request, method, uri, accessKey, secretKey, region: "us-east-1");
    }

    private static void SignAwsSigV4(
        HttpRequestMessage request,
        HttpMethod method,
        Uri uri,
        string accessKey,
        string secretKey,
        string region)
    {
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        const string emptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var host = uri.Authority;

        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", emptyPayloadHash);
        request.Headers.Host = host;

        var canonicalUri = uri.AbsolutePath;
        var canonicalQuery = uri.Query.TrimStart('?');
        var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{emptyPayloadHash}\nx-amz-date:{amzDate}\n";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";

        var canonicalRequest = $"{method.Method}\n{canonicalUri}\n{canonicalQuery}\n{canonicalHeaders}\n{signedHeaders}\n{emptyPayloadHash}";
        var canonicalRequestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));

        var credentialScope = $"{dateStamp}/{region}/s3/aws4_request";
        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n{canonicalRequestHash}";

        var kDate = HmacSha256(Encoding.UTF8.GetBytes($"AWS4{secretKey}"), Encoding.UTF8.GetBytes(dateStamp));
        var kRegion = HmacSha256(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HmacSha256(kRegion, Encoding.UTF8.GetBytes("s3"));
        var kSigning = HmacSha256(kService, Encoding.UTF8.GetBytes("aws4_request"));
        var signature = Convert.ToHexStringLower(HmacSha256(kSigning, Encoding.UTF8.GetBytes(stringToSign)));

        var authHeader = $"AWS4-HMAC-SHA256 Credential={accessKey}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
        request.Headers.TryAddWithoutValidation("Authorization", authHeader);
    }

    private static byte[] HmacSha256(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }
}
