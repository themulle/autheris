using Autheris.Application.Services;

namespace Autheris.Extensions.Lakehouse.Services;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Extensions.Lakehouse.Interfaces;
using Microsoft.Extensions.Logging;

using Autheris.Domain.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

/// <summary>
/// Parser and loader for Apache Iceberg v2 table metadata, manifest lists, and data manifests.
/// </summary>
public sealed class IcebergMetadataReader : IIcebergMetadataReader
{
    private readonly ILakehouseStorageProvider _storageProvider;
    private readonly IMemoryCache? _memoryCache;
    private readonly IOptions<GatewayOptions>? _options;
    private readonly ILogger<IcebergMetadataReader> _logger;

    public IcebergMetadataReader(
        ILakehouseStorageProvider storageProvider,
        ILogger<IcebergMetadataReader> logger,
        IMemoryCache? memoryCache = null,
        IOptions<GatewayOptions>? options = null)
    {
        _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _memoryCache = memoryCache;
        _options = options;
    }

    public async ValueTask<IcebergTableMetadata> LoadTableMetadataAsync(
        string metadataLocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataLocation);

        var cacheKey = $"iceberg:meta:{metadataLocation}";
        if (_memoryCache != null && _memoryCache.TryGetValue(cacheKey, out IcebergTableMetadata? cachedMeta) && cachedMeta != null)
        {
            _logger.LogDebug("Cache hit for Iceberg table metadata: {Location}", metadataLocation);
            return cachedMeta;
        }

        var jsonText = await _storageProvider.ReadTextAsync(metadataLocation, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(jsonText);
        var root = doc.RootElement;

        var tableUuid = root.TryGetProperty("table-uuid", out var uuidProp) ? uuidProp.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
        var formatVersion = root.TryGetProperty("format-version", out var formatProp) ? formatProp.GetInt32() : 2;
        var location = root.TryGetProperty("location", out var locProp) ? locProp.GetString() ?? "" : "";
        var lastSeq = root.TryGetProperty("last-sequence-number", out var seqProp) ? seqProp.GetInt64() : 0L;
        var lastUpdated = root.TryGetProperty("last-updated-ms", out var updatedProp) ? updatedProp.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var currentSnapshotId = root.TryGetProperty("current-snapshot-id", out var snapProp) ? snapProp.GetInt64() : -1L;

        // Parse Current Schema
        var schemaFields = new List<IcebergField>();
        if (root.TryGetProperty("schemas", out var schemasProp) && schemasProp.ValueKind == JsonValueKind.Array)
        {
            var targetSchemaId = root.TryGetProperty("current-schema-id", out var curSchemaId) ? curSchemaId.GetInt32() : 0;
            foreach (var schemaElem in schemasProp.EnumerateArray())
            {
                var sId = schemaElem.TryGetProperty("schema-id", out var sid) ? sid.GetInt32() : 0;
                if (sId == targetSchemaId && schemaElem.TryGetProperty("fields", out var fieldsProp))
                {
                    foreach (var f in fieldsProp.EnumerateArray())
                    {
                        var fid = f.GetProperty("id").GetInt32();
                        var fname = f.GetProperty("name").GetString() ?? "";
                        var ftype = f.GetProperty("type").GetString() ?? "string";
                        var freq = f.TryGetProperty("required", out var r) && r.GetBoolean();
                        schemaFields.Add(new IcebergField(fid, fname, ftype, freq));
                    }
                    break;
                }
            }
        }

        // Parse Partition Spec
        var partitionFields = new List<IcebergPartitionField>();
        if (root.TryGetProperty("partition-specs", out var specsProp) && specsProp.ValueKind == JsonValueKind.Array)
        {
            var defaultSpecId = root.TryGetProperty("default-spec-id", out var defSpec) ? defSpec.GetInt32() : 0;
            foreach (var specElem in specsProp.EnumerateArray())
            {
                var spId = specElem.TryGetProperty("spec-id", out var sp) ? sp.GetInt32() : 0;
                if (spId == defaultSpecId && specElem.TryGetProperty("fields", out var pFields))
                {
                    foreach (var pf in pFields.EnumerateArray())
                    {
                        var sourceId = pf.GetProperty("source-id").GetInt32();
                        var fieldId = pf.GetProperty("field-id").GetInt32();
                        var name = pf.GetProperty("name").GetString() ?? "";
                        var transform = pf.GetProperty("transform").GetString() ?? "identity";
                        partitionFields.Add(new IcebergPartitionField(sourceId, fieldId, name, transform));
                    }
                    break;
                }
            }
        }

        // Parse Snapshots
        var snapshots = new List<IcebergSnapshot>();
        if (root.TryGetProperty("snapshots", out var snapsProp) && snapsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var snapElem in snapsProp.EnumerateArray())
            {
                var sId = snapElem.GetProperty("snapshot-id").GetInt64();
                var timestamp = snapElem.GetProperty("timestamp-ms").GetInt64();
                var manifestList = snapElem.GetProperty("manifest-list").GetString() ?? "";
                snapshots.Add(new IcebergSnapshot(sId, timestamp, manifestList));
            }
        }

        var schema = new IcebergSchema(0, schemaFields);
        var partitionSpec = new IcebergPartitionSpec(0, partitionFields);

        _logger.LogInformation("Loaded Iceberg table {TableUuid} (Format: v{FormatVersion}, CurrentSnapshotId: {SnapshotId}, Columns: {ColCount}).",
            tableUuid, formatVersion, currentSnapshotId, schemaFields.Count);

        var result = new IcebergTableMetadata(
            tableUuid,
            formatVersion,
            location,
            lastSeq,
            lastUpdated,
            currentSnapshotId,
            schema,
            partitionSpec,
            snapshots
        );

        if (_memoryCache != null)
        {
            var ttlMinutes = _options?.Value?.Lakehouse?.MetadataCacheTtlMinutes ?? 15;
            var entryOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(ttlMinutes),
                Size = 1
            };
            _memoryCache.Set(cacheKey, result, entryOptions);
        }

        return result;
    }

    public ValueTask<IReadOnlyList<IcebergDataFile>> LoadDataFilesAsync(
        IcebergTableMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        // Without a configured trust anchor the caller-supplied metadata location is used (caller must trust 'metadata').
        return LoadDataFilesAsync(metadata, metadata.Location, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<IcebergDataFile>> LoadDataFilesAsync(
        IcebergTableMetadata metadata,
        string configuredTableLocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        // SEC M-33/M-35: the configured table location (not 'location'/'table-uuid' from the metadata file) is the trust anchor
        // for manifest/data file containment and for the manifest cache key.
        var trustAnchor = string.IsNullOrWhiteSpace(configuredTableLocation) ? string.Empty : configuredTableLocation.Trim();
        var manifestCacheKey = $"iceberg:manifest:{trustAnchor}:{metadata.CurrentSnapshotId}";
        if (_memoryCache != null && _memoryCache.TryGetValue(manifestCacheKey, out IReadOnlyList<IcebergDataFile>? cachedFiles) && cachedFiles != null)
        {
            _logger.LogDebug("Cache hit for Iceberg manifest files: {SnapshotId}", metadata.CurrentSnapshotId);
            return cachedFiles;
        }

        var dataFiles = new List<IcebergDataFile>();

        // Find current snapshot
        IcebergSnapshot? currentSnap = null;
        foreach (var s in metadata.Snapshots)
        {
            if (s.SnapshotId == metadata.CurrentSnapshotId)
            {
                currentSnap = s;
                break;
            }
        }

        if (currentSnap == null || string.IsNullOrWhiteSpace(currentSnap.ManifestListLocation))
        {
            _logger.LogWarning("Iceberg table has no current snapshot or manifest list.");
            return dataFiles;
        }

        ValidateManifestLocation(currentSnap.ManifestListLocation, trustAnchor);

        // Check if manifest list exists
        if (!await _storageProvider.ExistsAsync(currentSnap.ManifestListLocation, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Iceberg manifest list '{Location}' not found in storage.", currentSnap.ManifestListLocation);
            return dataFiles;
        }

        var manifestListJson = await _storageProvider.ReadTextAsync(currentSnap.ManifestListLocation, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(manifestListJson);
        var root = doc.RootElement;

        // The manifest list contains references to manifest files or direct data files
        if (root.TryGetProperty("entries", out var entriesProp) && entriesProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entriesProp.EnumerateArray())
            {
                var filePath = entry.GetProperty("file_path").GetString() ?? "";
                ValidateManifestLocation(filePath, trustAnchor);
                var fileFormat = entry.TryGetProperty("file_format", out var ff) ? ff.GetString() ?? "PARQUET" : "PARQUET";
                var recordCount = entry.TryGetProperty("record_count", out var rc) ? rc.GetInt64() : 1000L;
                var fileSizeBytes = entry.TryGetProperty("file_size_in_bytes", out var fs) ? fs.GetInt64() : 1024L;

                var partitions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (entry.TryGetProperty("partition", out var partProp) && partProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in partProp.EnumerateObject())
                    {
                        partitions[p.Name] = p.Value.GetString() ?? "";
                    }
                }

                var lowerBounds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (entry.TryGetProperty("lower_bounds", out var lbProp) && lbProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in lbProp.EnumerateObject())
                    {
                        lowerBounds[p.Name] = p.Value.GetString() ?? "";
                    }
                }

                var upperBounds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (entry.TryGetProperty("upper_bounds", out var ubProp) && ubProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in ubProp.EnumerateObject())
                    {
                        upperBounds[p.Name] = p.Value.GetString() ?? "";
                    }
                }

                dataFiles.Add(new IcebergDataFile(
                    filePath,
                    fileFormat,
                    partitions,
                    recordCount,
                    fileSizeBytes,
                    lowerBounds,
                    upperBounds
                ));
            }
        }

        _logger.LogInformation("Loaded {Count} Iceberg data files from manifest list for snapshot {SnapshotId}.",
            dataFiles.Count, currentSnap.SnapshotId);

        if (_memoryCache != null)
        {
            var ttlMinutes = _options?.Value?.Lakehouse?.MetadataCacheTtlMinutes ?? 15;
            var entryOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(ttlMinutes),
                Size = 1
            };
            _memoryCache.Set(manifestCacheKey, (IReadOnlyList<IcebergDataFile>)dataFiles, entryOptions);
        }

        return dataFiles;
    }

    internal static void ValidateManifestLocation(string manifestLocation, string? tableLocation)
    {
        if (string.IsNullOrWhiteSpace(manifestLocation)) return;

        if (manifestLocation.Contains('\0'))
        {
            throw new System.Security.SecurityException($"Access to restricted path '{manifestLocation}' is strictly forbidden.");
        }

        // SEC M-33: '.'/'..' segments (also percent-encoded) are never allowed in manifest or data file references.
        if (LakehouseLocationGuard.ContainsTraversal(manifestLocation))
        {
            throw new System.Security.SecurityException($"Path traversal in manifest/data file reference '{manifestLocation}' is forbidden.");
        }

        var tableHasScheme = !string.IsNullOrWhiteSpace(tableLocation) && tableLocation.Contains("://", StringComparison.Ordinal);

        if (manifestLocation.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(manifestLocation, UriKind.Absolute, out var manifestUri))
            {
                throw new System.Security.SecurityException($"Manifest/data file reference '{manifestLocation}' is not a valid absolute URI.");
            }

            if (manifestUri.Scheme == "file")
            {
                EnsureNotRestrictedLocalPath(manifestUri.LocalPath, manifestLocation);

                if (string.IsNullOrWhiteSpace(tableLocation))
                {
                    return;
                }

                string? tableDirectory = null;
                if (!tableHasScheme)
                {
                    tableDirectory = GetTableDirectory(tableLocation!);
                }
                else if (Uri.TryCreate(tableLocation, UriKind.Absolute, out var tableBaseUri) && tableBaseUri.Scheme == "file")
                {
                    tableDirectory = GetTableDirectory(tableBaseUri.LocalPath);
                }

                if (tableDirectory == null)
                {
                    throw new System.Security.SecurityException(
                        $"Cross-scheme reference to 'file' from table location '{tableLocation}' is forbidden.");
                }

                if (!IsWithinLocalDirectory(Path.GetFullPath(manifestUri.LocalPath), tableDirectory))
                {
                    throw new System.Security.SecurityException($"Manifest file '{manifestLocation}' must reside within table directory '{tableLocation}'.");
                }

                return;
            }

            var isHttp = manifestUri.Scheme == "http" || manifestUri.Scheme == "https";
            if (isHttp)
            {
                DeclarativeHttpDataSourceExecutor.ValidateUrl(manifestUri);
            }

            if (string.IsNullOrWhiteSpace(tableLocation))
            {
                if (isHttp)
                {
                    // SEC M-33: http(s) references are only accepted when they match a configured table location.
                    throw new System.Security.SecurityException(
                        $"http(s) manifest/data file reference '{manifestLocation}' requires a configured table location.");
                }

                return;
            }

            if (!tableHasScheme || !Uri.TryCreate(tableLocation, UriKind.Absolute, out var tableUri))
            {
                throw new System.Security.SecurityException(
                    $"Cross-scheme reference to '{manifestUri.Scheme}' from local table location '{tableLocation}' is forbidden.");
            }

            if (!AreSchemesCompatible(manifestUri.Scheme, tableUri.Scheme))
            {
                throw new System.Security.SecurityException(
                    $"Manifest/data file URI scheme '{manifestUri.Scheme}' does not match table location scheme '{tableUri.Scheme}'.");
            }

            // SEC M-33: same bucket/account/container (authority) and below the configured table prefix.
            if (!string.Equals(manifestUri.Host, tableUri.Host, StringComparison.OrdinalIgnoreCase) ||
                manifestUri.Port != tableUri.Port ||
                !string.Equals(manifestUri.UserInfo, tableUri.UserInfo, StringComparison.OrdinalIgnoreCase))
            {
                throw new System.Security.SecurityException(
                    $"Manifest/data file '{manifestLocation}' must reside in the same storage location as the configured table '{tableLocation}'.");
            }

            var tablePrefix = GetUriTableDirectory(tableUri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(tablePrefix) || tablePrefix == "/")
            {
                // SEC EX-11: Empty table prefix means table location points to root of storage/bucket, which is rejected.
                throw new System.Security.SecurityException(
                    $"Configured table location '{tableLocation}' resolves to an empty root prefix. Tables must reside within a dedicated directory.");
            }

            if (!manifestUri.AbsolutePath.StartsWith(tablePrefix + "/", StringComparison.Ordinal) &&
                !string.Equals(manifestUri.AbsolutePath, tablePrefix, StringComparison.Ordinal))
            {
                throw new System.Security.SecurityException(
                    $"Manifest/data file '{manifestLocation}' must reside within configured table location '{tableLocation}'.");
            }

            return;
        }

        EnsureNotRestrictedLocalPath(manifestLocation, manifestLocation);

        if (string.IsNullOrWhiteSpace(tableLocation))
        {
            return;
        }

        string localTableDirectory;
        if (!tableHasScheme)
        {
            localTableDirectory = GetTableDirectory(tableLocation);
        }
        else if (Uri.TryCreate(tableLocation, UriKind.Absolute, out var fileTableUri) && fileTableUri.Scheme == "file")
        {
            localTableDirectory = GetTableDirectory(fileTableUri.LocalPath);
        }
        else
        {
            // SEC M-33: a remote table must not reference local/relative files.
            throw new System.Security.SecurityException(
                $"Local manifest/data file reference '{manifestLocation}' is not permitted for remote table location '{tableLocation}'.");
        }

        var fullManifestPath = Path.IsPathRooted(manifestLocation)
            ? Path.GetFullPath(manifestLocation)
            : Path.GetFullPath(Path.Combine(localTableDirectory, manifestLocation));

        if (!IsWithinLocalDirectory(fullManifestPath, localTableDirectory))
        {
            throw new System.Security.SecurityException($"Manifest file '{manifestLocation}' must reside within table directory '{tableLocation}'.");
        }
    }

    private static void EnsureNotRestrictedLocalPath(string path, string original)
    {
        var normalized = path.Replace('\\', '/').ToLowerInvariant();
        if (normalized.Contains('\0') ||
            normalized.StartsWith("/etc", StringComparison.Ordinal) ||
            normalized.StartsWith("/proc", StringComparison.Ordinal) ||
            normalized.StartsWith("/sys", StringComparison.Ordinal) ||
            normalized.StartsWith("/dev", StringComparison.Ordinal) ||
            normalized.StartsWith("/var", StringComparison.Ordinal) ||
            normalized.StartsWith("/run", StringComparison.Ordinal) ||
            normalized.StartsWith("/root", StringComparison.Ordinal) ||
            normalized.StartsWith("/bin", StringComparison.Ordinal) ||
            normalized.StartsWith("/sbin", StringComparison.Ordinal) ||
            normalized.StartsWith("/usr", StringComparison.Ordinal) ||
            normalized.Contains("/.ssh/", StringComparison.Ordinal) ||
            normalized.Contains("windows/system32", StringComparison.Ordinal))
        {
            throw new System.Security.SecurityException($"Access to restricted path '{original}' is strictly forbidden.");
        }
    }

    private static bool IsWithinLocalDirectory(string fullPath, string directory)
    {
        var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, trimmed, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool AreSchemesCompatible(string manifestScheme, string tableScheme)
    {
        if (string.Equals(manifestScheme, tableScheme, StringComparison.OrdinalIgnoreCase)) return true;

        static bool IsAbfs(string scheme) =>
            string.Equals(scheme, "abfs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(scheme, "abfss", StringComparison.OrdinalIgnoreCase);

        return IsAbfs(manifestScheme) && IsAbfs(tableScheme);
    }

    /// <summary>
    /// Derives the table root prefix from a configured location path ("/orders/metadata/v2.metadata.json" -> "/orders").
    /// </summary>
    internal static string GetUriTableDirectory(string absolutePath)
    {
        var path = (absolutePath ?? string.Empty).TrimEnd('/');

        var lastSlash = path.LastIndexOf('/');
        var lastSegment = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        if (lastSegment.Contains('.', StringComparison.Ordinal))
        {
            path = lastSlash >= 0 ? path[..lastSlash] : string.Empty;
            lastSlash = path.LastIndexOf('/');
            lastSegment = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        }

        if (string.Equals(lastSegment, "metadata", StringComparison.OrdinalIgnoreCase))
        {
            path = lastSlash >= 0 ? path[..lastSlash] : string.Empty;
        }

        return path;
    }

    private static string GetTableDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (Path.HasExtension(fullPath) || File.Exists(fullPath))
        {
            fullPath = Path.GetDirectoryName(fullPath) ?? fullPath;
        }
        if (string.Equals(Path.GetFileName(fullPath), "metadata", StringComparison.OrdinalIgnoreCase))
        {
            fullPath = Path.GetDirectoryName(fullPath) ?? fullPath;
        }
        return fullPath;
    }
}
