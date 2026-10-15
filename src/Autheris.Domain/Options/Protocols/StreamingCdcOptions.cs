namespace Autheris.Domain.Options;

public sealed class MssqlChangeTrackingOptions
{
    public bool Enabled { get; init; } = false;
    public string ConnectionString { get; init; } = string.Empty;
    public int PollingIntervalMilliseconds { get; init; } = 1000;
    public int BatchSize { get; init; } = 500;
    public List<string> TrackedTables { get; init; } = [];
}

/// <summary>
/// F-CDC-03: Zero-Kafka PostgreSQL CDC via Logical Streaming Replication (pgoutput).
/// </summary>
public sealed class PostgreSqlCdcOptions
{
    public bool Enabled { get; init; } = false;
    public string ConnectionString { get; init; } = string.Empty;
    public string SlotName { get; init; } = "autheris_gateway_cdc_slot";
    public string PublicationName { get; init; } = "autheris_gateway_pub";
    public List<string> TrackedTables { get; init; } = [];
    public long MaxLagBytes { get; init; } = 1_000_000_000L; // 1 GB protection limit
    public int AckIntervalMilliseconds { get; init; } = 1000;
}
