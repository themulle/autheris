using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

public sealed class LakehouseStorageOptions
{
    public string Provider { get; init; } = "Local"; // "Local" | "S3" | "AzureBlob"
    public string LocalBasePath { get; init; } = string.Empty;
    public string S3Endpoint { get; init; } = string.Empty;
    public string S3Bucket { get; init; } = string.Empty;
    public string S3AccessKey { get; init; } = string.Empty;
    public string S3SecretKey { get; init; } = string.Empty;

    /// <summary>EXT-8: SigV4 signing region. Empty = derived from the endpoint host (s3.&lt;region&gt;.amazonaws.com), else us-east-1.</summary>
    public string S3Region { get; init; } = string.Empty;
    public string AzureAccountName { get; init; } = string.Empty;
    public string AzureContainer { get; init; } = string.Empty;
    public string AzureAccountKey { get; init; } = string.Empty;

    /// <summary>
    /// EXT-5: additional S3 buckets that may be addressed besides <see cref="S3Bucket"/> and the buckets of configured
    /// table locations. Without any allowed bucket every S3 request is refused (fail-closed).
    /// </summary>
    public List<string> S3AllowedBuckets { get; init; } = [];

    /// <summary>
    /// EXT-5: additional Azure containers that may be addressed besides <see cref="AzureContainer"/> and the containers of
    /// configured table locations. The account key signs requests for the whole account, so without any allowed container
    /// every Azure request is refused (fail-closed).
    /// </summary>
    public List<string> AzureAllowedContainers { get; init; } = [];

    /// <summary>SEC: Maximum number of bytes when reading metadata/manifest files (default 64 MB).</summary>
    public long MaxReadBytes { get; init; } = 64L * 1024 * 1024;
}

public sealed class LakehouseTableOptions
{
    public string Format { get; init; } = "Iceberg";
    public string Location { get; init; } = string.Empty;
    public List<string> PartitionColumns { get; init; } = [];
    public string Sensitivity { get; init; } = "LOW";
}

public sealed class LakehouseOptions
{
    public bool Enabled { get; init; } = false;
    [Range(1, 1440)] public int MetadataCacheTtlMinutes { get; init; } = 15;
    [Range(1, 64)] public int MaxConcurrentFileScans { get; init; } = 16;
    [Range(1, 500000)] public int MaxScanRowsLimit { get; init; } = 50000;
    public LakehouseStorageOptions Storage { get; init; } = new();
    public Dictionary<string, LakehouseTableOptions> Tables { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// F-DATA-03: Embedded In-Memory OLAP via DuckDB.NET Options.
/// </summary>
public sealed class DuckDbOlapOptions
{
    public bool Enabled { get; init; } = true;
    public string MaxMemory { get; init; } = "1GB";
    public int MaxStagedRowsPerTable { get; init; } = 250000;

    /// <summary>Maximum rows of an OLAP query result; a smaller requested limit is kept.</summary>
    public int MaxResultRows { get; init; } = 50000;

    /// <summary>Maximum byte size of an OLAP query result (default 32MB) to prevent memory exhaustion DoS (SG-02).</summary>
    public long MaxResultBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>Maximum distinct tables allowed to be staged in a single OLAP query (default 10) (SG-02).</summary>
    public int MaxTableCount { get; init; } = 10;

    /// <summary>Maximum total staged rows across all tables in a single OLAP query (default 500,000) (SG-02).</summary>
    public int MaxTotalStagedRows { get; init; } = 500000;

    /// <summary>Maximum concurrent OLAP query executions across all tenants (default 4) (SG-02).</summary>
    public int MaxConcurrentQueries { get; init; } = 4;

    /// <summary>Maximum size of the session-isolated temporary spill directory (default 256MB) (SG-01).</summary>
    public string MaxTempDirectorySize { get; init; } = "256MB";

    public int QueryTimeoutSeconds { get; init; } = 60;
    public int MaxThreads { get; init; } = 2;

    /// <summary>Maximum estimated bytes staged from one table (default 64MB). Applies to every OLAP request; cross-source requests use the smaller of this and the CrossSource value.</summary>
    public long MaxStagedBytesPerTable { get; init; } = 64L * 1024 * 1024;

    /// <summary>Maximum estimated bytes staged across all tables of one request (default 256MB). Cross-source requests use the smaller of this and the CrossSource value.</summary>
    public long MaxTotalStagedBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Maximum concurrent OLAP requests of one tenant (default 2); further requests are answered with 429.</summary>
    public int MaxConcurrentQueriesPerTenant { get; init; } = 2;

    /// <summary>Maximum length of the client SQL text in characters (default 32768).</summary>
    public int MaxSqlLength { get; init; } = 32768;
}
