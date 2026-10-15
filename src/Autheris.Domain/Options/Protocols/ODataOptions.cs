namespace Autheris.Domain.Options;

public sealed class ODataOptions
{
    public bool Enabled { get; init; } = true;
}

public sealed class ParquetEgressOptions
{
    public bool Enabled { get; init; } = true;
    public int MaxRowsPerFile { get; init; } = 100000;
    public bool FlattenNestedStructures { get; init; } = true;

    /// <summary>Parquet column compression codec: None | Snappy | Gzip (invalid values fall back to Snappy).</summary>
    public string Compression { get; init; } = "Snappy";

    /// <summary>Maximum size of a buffered JSON source response (GraphQL) that is converted to Parquet.</summary>
    public long MaxBufferedSourceBytes { get; init; } = 64 * 1024 * 1024;
}

/// <summary>
/// F-DATA-04: Native Apache Arrow IPC &amp; Flight SQL Egress Options.
/// </summary>
public sealed class ArrowExportOptions
{
    public bool Enabled { get; init; } = true;
    public int BatchSize { get; init; } = 64000;
    public int MaxExportRows { get; init; } = 1000000;
    public bool EnableFlightSql { get; init; } = true;

    /// <summary>
    /// SEC (Low): dedicated HMAC key (&gt;= 32 characters) for signing Flight SQL tickets. When empty, a random per-process key
    /// is used (tickets do not survive restarts or work across replicas) and a warning is logged.
    /// </summary>
    public string? FlightTicketSigningKey { get; init; }
}
