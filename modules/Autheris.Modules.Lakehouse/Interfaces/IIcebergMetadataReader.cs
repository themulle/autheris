namespace Autheris.Extensions.Lakehouse.Interfaces;

using Autheris.Modules.Lakehouse.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// Parser and loader for Apache Iceberg v2 table metadata, manifest lists, and data manifests.
/// </summary>
public interface IIcebergMetadataReader
{
    /// <summary>
    /// Resolves a table root directory or explicit metadata file path to the latest Iceberg metadata file location.
    /// </summary>
    ValueTask<string> ResolveMetadataLocationAsync(
        string tableOrMetadataLocation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads and parses the root table metadata (e.g. v2.metadata.json).
    /// </summary>
    ValueTask<IcebergTableMetadata> LoadTableMetadataAsync(
        string metadataLocation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Traverses the current snapshot's manifest list and returns all active data files.
    /// </summary>
    ValueTask<IReadOnlyList<IcebergDataFile>> LoadDataFilesAsync(
        IcebergTableMetadata metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Traverses the current snapshot's manifest list using the configured table location as trust anchor:
    /// manifest/data files must reside below it and it (not 'table-uuid' from the file) keys the manifest cache.
    /// </summary>
    ValueTask<IReadOnlyList<IcebergDataFile>> LoadDataFilesAsync(
        IcebergTableMetadata metadata,
        string configuredTableLocation,
        CancellationToken cancellationToken = default);
}
