namespace Autheris.Domain.Options;

/// <summary>OData v4 protocol endpoint options (<c>Gateway:OData</c>).</summary>
public sealed class ODataOptions
{
    /// <summary>Enables OData v4 metadata and entity set endpoints.</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>Apache Parquet egress options (<c>Gateway:ParquetEgress</c>).</summary>
public sealed class ParquetEgressOptions
{
    /// <summary>Maximum number of rows written per Parquet output file.</summary>
    public int MaxRowsPerFile { get; init; } = 100000;

    /// <summary>When true, flattens nested object structures into top-level Parquet columns.</summary>
    public bool FlattenNestedStructures { get; init; } = true;

    /// <summary>Parquet column compression codec: None | Snappy | Gzip (invalid values fall back to Snappy).</summary>
    public string Compression { get; init; } = "Snappy";

    /// <summary>Maximum size of a buffered JSON source response (GraphQL) that is converted to Parquet.</summary>
    public long MaxBufferedSourceBytes { get; init; } = 64 * 1024 * 1024;
}

/// <summary>
/// F-DATA-04: Native Apache Arrow IPC Egress Options.
/// </summary>
public sealed class ArrowExportOptions
{
    /// <summary>Number of rows serialized per Arrow IPC record batch.</summary>
    public int BatchSize { get; init; } = 64000;

    /// <summary>Maximum number of rows permitted in a single Arrow IPC export.</summary>
    public int MaxExportRows { get; init; } = 1000000;
}
