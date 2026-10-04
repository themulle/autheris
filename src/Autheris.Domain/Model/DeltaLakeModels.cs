namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// P11: Modern Lakehouse Connector - Delta Lake table metadata and transaction log models.
/// </summary>
public sealed record DeltaTableMetadata(
    string Id,
    string? Name,
    string? Description,
    string FormatProvider,
    DeltaSchema Schema,
    IReadOnlyList<string> PartitionColumns,
    long CreatedTimeMs,
    IReadOnlyDictionary<string, string> Configuration
);

/// <summary>
/// Delta Lake schema definition parsed from table metadata action.
/// </summary>
public sealed record DeltaSchema(
    string Type,
    IReadOnlyList<DeltaField> Fields
);

/// <summary>
/// Column field within a Delta Lake schema.
/// </summary>
public sealed record DeltaField(
    string Name,
    string Type,
    bool Nullable = true,
    IReadOnlyDictionary<string, object?>? Metadata = null
);

/// <summary>
/// Represents a reconstructed snapshot of a Delta Lake table at a specific version.
/// </summary>
public sealed record DeltaSnapshot(
    string TableLocation,
    long Version,
    long TimestampMs,
    DeltaTableMetadata Metadata,
    IReadOnlyList<DeltaDataFile> ActiveFiles,
    DeltaProtocol Protocol
);

/// <summary>
/// Delta protocol action specifying minimum reader and writer versions.
/// </summary>
public sealed record DeltaProtocol(
    int MinReaderVersion,
    int MinWriterVersion
);

/// <summary>
/// Represents an active Parquet data file referenced by an 'add' action in the Delta log.
/// </summary>
public sealed record DeltaDataFile(
    string Path,
    IReadOnlyDictionary<string, string> PartitionValues,
    long SizeBytes,
    long ModificationTimeMs,
    bool DataChange,
    long RecordCount = 0,
    IReadOnlyDictionary<string, string>? MinValues = null,
    IReadOnlyDictionary<string, string>? MaxValues = null,
    IReadOnlyDictionary<string, long>? NullCounts = null
);

/// <summary>
/// Delta UniForm (Universal Format) metadata compatibility mode.
/// </summary>
public enum DeltaUniFormCompatibility
{
    DeltaNative = 0,
    IcebergCompat = 1,
    HudiCompat = 2
}
