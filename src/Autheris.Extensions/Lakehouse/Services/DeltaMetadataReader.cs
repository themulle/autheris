namespace Autheris.Extensions.Lakehouse.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;
using Autheris.Extensions.Lakehouse.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// P11: Modern Lakehouse Connector - Parser and snapshot loader for Delta Lake transaction logs.
/// </summary>
public sealed class DeltaMetadataReader : IDeltaMetadataReader
{
    private readonly ILakehouseStorageProvider _storageProvider;
    private readonly ILogger<DeltaMetadataReader> _logger;

    public DeltaMetadataReader(
        ILakehouseStorageProvider storageProvider,
        ILogger<DeltaMetadataReader> logger)
    {
        _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<DeltaSnapshot> LoadSnapshotAsync(
        string tableLocation,
        long? asOfVersion = null,
        DateTimeOffset? asOfTimestamp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableLocation);

        tableLocation = tableLocation.TrimEnd('/');
        var deltaLogDir = $"{tableLocation}/_delta_log";

        long currentVersion = 0;
        DeltaProtocol? protocol = null;
        DeltaTableMetadata? metadata = null;
        var activeFiles = new Dictionary<string, DeltaDataFile>(StringComparer.Ordinal);
        long lastTimestampMs = 0;

        while (true)
        {
            if (asOfVersion.HasValue && currentVersion > asOfVersion.Value)
            {
                break;
            }

            var commitFileName = $"{currentVersion:D20}.json";
            var commitPath = $"{deltaLogDir}/{commitFileName}";

            var exists = await _storageProvider.ExistsAsync(commitPath, cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                if (currentVersion == 0)
                {
                    throw new FileNotFoundException($"Delta transaction log not found at '{commitPath}'.");
                }
                break;
            }

            var commitContent = await _storageProvider.ReadTextAsync(commitPath, cancellationToken).ConfigureAwait(false);
            using var reader = new StringReader(commitContent);
            string? line;

            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("protocol", out var protoEl))
                {
                    var minReader = protoEl.TryGetProperty("minReaderVersion", out var mr) ? mr.GetInt32() : 1;
                    var minWriter = protoEl.TryGetProperty("minWriterVersion", out var mw) ? mw.GetInt32() : 2;
                    protocol = new DeltaProtocol(minReader, minWriter);
                }

                if (root.TryGetProperty("metaData", out var metaEl))
                {
                    var id = metaEl.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var name = metaEl.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                    var desc = metaEl.TryGetProperty("description", out var dEl) ? dEl.GetString() : null;
                    var formatProvider = metaEl.TryGetProperty("format", out var fEl) && fEl.TryGetProperty("provider", out var pEl) ? pEl.GetString() ?? "parquet" : "parquet";
                    var createdTime = metaEl.TryGetProperty("createdTime", out var ctEl) ? ctEl.GetInt64() : 0;

                    var partitionCols = new List<string>();
                    if (metaEl.TryGetProperty("partitionColumns", out var pcEl) && pcEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var col in pcEl.EnumerateArray())
                        {
                            var s = col.GetString();
                            if (!string.IsNullOrEmpty(s)) partitionCols.Add(s);
                        }
                    }

                    var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (metaEl.TryGetProperty("configuration", out var confEl) && confEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in confEl.EnumerateObject())
                        {
                            config[prop.Name] = prop.Value.GetString() ?? "";
                        }
                    }

                    var schemaFields = new List<DeltaField>();
                    if (metaEl.TryGetProperty("schemaString", out var schemaStrEl))
                    {
                        var schemaJson = schemaStrEl.GetString();
                        if (!string.IsNullOrEmpty(schemaJson))
                        {
                            try
                            {
                                using var schemaDoc = JsonDocument.Parse(schemaJson);
                                if (schemaDoc.RootElement.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var f in fieldsEl.EnumerateArray())
                                    {
                                        var fname = f.TryGetProperty("name", out var fn) ? fn.GetString() ?? "" : "";
                                        var ftype = f.TryGetProperty("type", out var ft) ? ft.GetString() ?? "string" : "string";
                                        var fnull = !f.TryGetProperty("nullable", out var fl) || fl.GetBoolean();
                                        schemaFields.Add(new DeltaField(fname, ftype, fnull));
                                    }
                                }
                            }
                            catch (JsonException)
                            {
                                // Malformed schema fallback
                            }
                        }
                    }

                    metadata = new DeltaTableMetadata(
                        id,
                        name,
                        desc,
                        formatProvider,
                        new DeltaSchema("struct", schemaFields),
                        partitionCols,
                        createdTime,
                        config);
                }

                if (root.TryGetProperty("add", out var addEl))
                {
                    var path = addEl.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? "" : "";
                    
                    // SEC-DL-01: Path traversal validation
                    LakehouseLocationGuard.EnsureNoTraversal(path, tableLocation);

                    var size = addEl.TryGetProperty("size", out var sEl) ? sEl.GetInt64() : 0;
                    var modTime = addEl.TryGetProperty("modificationTime", out var mtEl) ? mtEl.GetInt64() : 0;
                    var dataChange = !addEl.TryGetProperty("dataChange", out var dcEl) || dcEl.GetBoolean();
                    lastTimestampMs = Math.Max(lastTimestampMs, modTime);

                    var partitionVals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (addEl.TryGetProperty("partitionValues", out var pvEl) && pvEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in pvEl.EnumerateObject())
                        {
                            partitionVals[prop.Name] = prop.Value.GetString() ?? "";
                        }
                    }

                    Dictionary<string, string>? minVals = null;
                    Dictionary<string, string>? maxVals = null;
                    long numRecords = 0;

                    if (addEl.TryGetProperty("stats", out var statsEl) && statsEl.ValueKind == JsonValueKind.String)
                    {
                        var statsJson = statsEl.GetString();
                        if (!string.IsNullOrEmpty(statsJson))
                        {
                            try
                            {
                                using var statsDoc = JsonDocument.Parse(statsJson);
                                if (statsDoc.RootElement.TryGetProperty("numRecords", out var nrEl))
                                {
                                    numRecords = nrEl.GetInt64();
                                }
                                if (statsDoc.RootElement.TryGetProperty("minValues", out var mnEl) && mnEl.ValueKind == JsonValueKind.Object)
                                {
                                    minVals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                    foreach (var p in mnEl.EnumerateObject())
                                    {
                                        minVals[p.Name] = p.Value.ToString();
                                    }
                                }
                                if (statsDoc.RootElement.TryGetProperty("maxValues", out var mxEl) && mxEl.ValueKind == JsonValueKind.Object)
                                {
                                    maxVals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                    foreach (var p in mxEl.EnumerateObject())
                                    {
                                        maxVals[p.Name] = p.Value.ToString();
                                    }
                                }
                            }
                            catch (JsonException) { }
                        }
                    }

                    activeFiles[path] = new DeltaDataFile(
                        path,
                        partitionVals,
                        size,
                        modTime,
                        dataChange,
                        numRecords,
                        minVals,
                        maxVals);
                }

                if (root.TryGetProperty("remove", out var remEl))
                {
                    var path = remEl.TryGetProperty("path", out var pEl) ? pEl.GetString() ?? "" : "";
                    
                    // SEC-DL-01: Path traversal validation
                    LakehouseLocationGuard.EnsureNoTraversal(path, tableLocation);

                    activeFiles.Remove(path);
                }
            }

            if (asOfTimestamp.HasValue && lastTimestampMs > asOfTimestamp.Value.ToUnixTimeMilliseconds())
            {
                break;
            }

            currentVersion++;
        }

        var resolvedVersion = currentVersion > 0 ? currentVersion - 1 : 0;
        if (asOfVersion.HasValue)
        {
            resolvedVersion = asOfVersion.Value;
        }

        protocol ??= new DeltaProtocol(1, 2);
        metadata ??= new DeltaTableMetadata(
            Guid.NewGuid().ToString("D"),
            Path.GetFileName(tableLocation),
            null,
            "parquet",
            new DeltaSchema("struct", Array.Empty<DeltaField>()),
            Array.Empty<string>(),
            0,
            new Dictionary<string, string>());

        var activeFileList = new List<DeltaDataFile>(activeFiles.Values);

        _logger.LogInformation("Loaded Delta table snapshot at '{TableLocation}' (Version: {Version}, ActiveFiles: {Count}).",
            tableLocation, resolvedVersion, activeFileList.Count);

        return new DeltaSnapshot(
            tableLocation,
            resolvedVersion,
            lastTimestampMs,
            metadata,
            activeFileList,
            protocol);
    }

    public async ValueTask<IReadOnlyList<DeltaDataFile>> LoadActiveFilesAsync(
        string tableLocation,
        long? asOfVersion = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadSnapshotAsync(tableLocation, asOfVersion, null, cancellationToken).ConfigureAwait(false);
        return snapshot.ActiveFiles;
    }
}
