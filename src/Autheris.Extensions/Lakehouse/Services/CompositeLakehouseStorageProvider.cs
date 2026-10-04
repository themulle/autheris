namespace Autheris.Extensions.Lakehouse.Services;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Microsoft.Extensions.Options;

/// <summary>
/// Composite dispatcher routing Lakehouse storage requests to Local, S3, or Azure Blob storage providers
/// based on URI protocol scheme (file://, s3://, minio://, abfss://, azure://) or configured default provider.
/// </summary>
public sealed class CompositeLakehouseStorageProvider : ILakehouseStorageProvider
{
    private readonly LocalStorageProvider _localStorage;
    private readonly S3LakehouseStorageProvider _s3Storage;
    private readonly AzureBlobStorageProvider _azureStorage;
    private readonly IOptions<GatewayOptions> _options;

    public CompositeLakehouseStorageProvider(
        LocalStorageProvider localStorage,
        S3LakehouseStorageProvider s3Storage,
        AzureBlobStorageProvider azureStorage,
        IOptions<GatewayOptions> options)
    {
        _localStorage = localStorage ?? throw new ArgumentNullException(nameof(localStorage));
        _s3Storage = s3Storage ?? throw new ArgumentNullException(nameof(s3Storage));
        _azureStorage = azureStorage ?? throw new ArgumentNullException(nameof(azureStorage));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ValueTask<string> ReadTextAsync(string location, CancellationToken cancellationToken = default)
    {
        var provider = SelectProvider(location);
        return provider.ReadTextAsync(location, cancellationToken);
    }

    public ValueTask<Stream> OpenReadStreamAsync(string location, CancellationToken cancellationToken = default)
    {
        var provider = SelectProvider(location);
        return provider.OpenReadStreamAsync(location, cancellationToken);
    }

    public ValueTask<bool> ExistsAsync(string location, CancellationToken cancellationToken = default)
    {
        var provider = SelectProvider(location);
        return provider.ExistsAsync(location, cancellationToken);
    }

    private ILakehouseStorageProvider SelectProvider(string location)
    {
        if (string.IsNullOrWhiteSpace(location)) return _localStorage;

        if (location.StartsWith("s3://", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith("minio://", StringComparison.OrdinalIgnoreCase))
        {
            return _s3Storage;
        }

        if (location.StartsWith("abfss://", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith("abfs://", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith("azure://", StringComparison.OrdinalIgnoreCase))
        {
            return _azureStorage;
        }

        // SEC H-18: route http(s) locations by the parsed host, never by substring matching.
        if (Uri.TryCreate(location, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
        {
            if (uri.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".dfs.core.windows.net", StringComparison.OrdinalIgnoreCase))
            {
                // The Azure provider additionally enforces the exact configured account host.
                return _azureStorage;
            }

            // The S3 provider enforces the configured endpoint / dot-anchored amazonaws.com host allowlist.
            return _s3Storage;
        }

        if (location.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            Path.IsPathRooted(location) ||
            location.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !location.Contains("://", StringComparison.Ordinal))
        {
            return _localStorage;
        }

        // Fall back to configured Lakehouse storage provider
        var configured = _options.Value.Lakehouse.Storage.Provider;
        if (string.Equals(configured, "S3", StringComparison.OrdinalIgnoreCase))
        {
            return _s3Storage;
        }

        if (string.Equals(configured, "AzureBlob", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configured, "ADLS", StringComparison.OrdinalIgnoreCase))
        {
            return _azureStorage;
        }

        return _localStorage;
    }
}
