namespace Autheris.Extensions.Lakehouse.Interfaces;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// P11: Parser and snapshot loader for Delta Lake transaction logs (_delta_log/*.json).
/// </summary>
public interface IDeltaMetadataReader
{
    /// <summary>
    /// Reconstructs the Delta table snapshot at the specified version or timestamp (time travel).
    /// </summary>
    ValueTask<DeltaSnapshot> LoadSnapshotAsync(
        string tableLocation,
        long? asOfVersion = null,
        DateTimeOffset? asOfTimestamp = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the latest active data files from the Delta table transaction log.
    /// </summary>
    ValueTask<IReadOnlyList<DeltaDataFile>> LoadActiveFilesAsync(
        string tableLocation,
        long? asOfVersion = null,
        CancellationToken cancellationToken = default);
}
