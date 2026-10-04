namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// Root Apache Iceberg table metadata parsed from v2.metadata.json.
/// </summary>
public sealed record IcebergTableMetadata(
    string TableUuid,
    int FormatVersion,
    string Location,
    long LastSequenceNumber,
    long LastUpdatedMs,
    long CurrentSnapshotId,
    IcebergSchema CurrentSchema,
    IcebergPartitionSpec PartitionSpec,
    IReadOnlyList<IcebergSnapshot> Snapshots
);

/// <summary>
/// Represents an Apache Iceberg schema definition.
/// </summary>
public sealed record IcebergSchema(
    int SchemaId,
    IReadOnlyList<IcebergField> Fields
);

/// <summary>
/// Field within an Apache Iceberg schema.
/// </summary>
public sealed record IcebergField(
    int Id,
    string Name,
    string Type,
    bool Required = false
);

/// <summary>
/// Partition specification for an Apache Iceberg table.
/// </summary>
public sealed record IcebergPartitionSpec(
    int SpecId,
    IReadOnlyList<IcebergPartitionField> Fields
);

/// <summary>
/// Individual partition field definition.
/// </summary>
public sealed record IcebergPartitionField(
    int SourceId,
    int FieldId,
    string Name,
    string Transform // e.g. "identity", "day", "month", "bucket"
);

/// <summary>
/// Snapshot entry in the Iceberg table history.
/// </summary>
public sealed record IcebergSnapshot(
    long SnapshotId,
    long TimestampMs,
    string ManifestListLocation,
    IReadOnlyDictionary<string, string>? Summary = null
);

/// <summary>
/// Represents a Parquet data file referenced in an Iceberg manifest.
/// </summary>
public sealed record IcebergDataFile(
    string FilePath,
    string FileFormat,
    IReadOnlyDictionary<string, string> PartitionValues,
    long RecordCount,
    long FileSizeBytes,
    IReadOnlyDictionary<string, string>? LowerBounds = null,
    IReadOnlyDictionary<string, string>? UpperBounds = null
);

/// <summary>
/// Scan request executed against an Iceberg Lakehouse table.
/// </summary>
public sealed record LakehouseScanRequest(
    string TableName,
    IReadOnlyList<string> SelectedColumns,
    IReadOnlyDictionary<string, string> FilterPredicates,
    string TenantId,
    int Limit = 1000
);

/// <summary>
/// Execution metrics and result rows from a Lakehouse query.
/// </summary>
public sealed record LakehouseScanResult(
    string TableName,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int TotalScannedFiles,
    int TotalPrunedFiles,
    double PruningEfficiencyPercent,
    TimeSpan ExecutionDuration
);
