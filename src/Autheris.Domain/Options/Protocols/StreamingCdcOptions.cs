namespace Autheris.Domain.Options;

using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>
/// F-CDC-02 / REQ-CDC-99: SQL Server Change Tracking producer. <see cref="TrackedTables"/> (shorthand, defaults apply) and
/// <see cref="Tables"/> (per-table overrides) are merged; <see cref="Tables"/> wins per table and duplicates are rejected by the validator.
/// </summary>
public sealed class MssqlChangeTrackingOptions
{
    public bool Enabled { get; init; } = false;

    /// <summary>Optional explicit source connection string; when empty the table's catalogued source connection is used.</summary>
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>Minimum 250.</summary>
    public int PollingIntervalMilliseconds { get; init; } = 1000;

    /// <summary>1..5000.</summary>
    public int BatchSize { get; init; } = 500;

    /// <summary>1..300.</summary>
    public int QueryTimeoutSeconds { get; init; } = 30;

    /// <summary>Opt-out of the SNAPSHOT isolation requirement of the source read (documented risk).</summary>
    public bool AllowNonSnapshotRead { get; init; } = false;

    public CdcOnWatermarkExpired OnWatermarkExpired { get; init; } = CdcOnWatermarkExpired.Restart;

    public List<string> TrackedTables { get; init; } = [];

    public List<CdcTrackedTableOptions> Tables { get; init; } = [];
}

/// <summary>Per-table override of the <see cref="MssqlChangeTrackingOptions"/> defaults; null members inherit.</summary>
public sealed class CdcTrackedTableOptions
{
    /// <summary>Qualified table name (<c>domain.schema.table</c>).</summary>
    public string Table { get; init; } = string.Empty;

    /// <summary>Optional data source name; when empty the catalog's source of the table is used.</summary>
    public string? DataSource { get; init; }

    public int? PollingIntervalMilliseconds { get; init; }
    public int? BatchSize { get; init; }
    public int? QueryTimeoutSeconds { get; init; }
    public int? RetentionHours { get; init; }
    public int? RetentionMaxEvents { get; init; }
    public CdcOnWatermarkExpired? OnWatermarkExpired { get; init; }
}

/// <summary><c>Gateway:CdcStream</c>: durable governed CDC stream (PLAN-CDC-99 section 3.4 and Phase-3 amendments).</summary>
public sealed class CdcStreamOptions
{
    public int RetentionHours { get; init; } = 72;
    public long RetentionMaxEvents { get; init; } = 1_000_000;
    public int RetentionSweepSeconds { get; init; } = 300;
    public int RetentionDeleteBatch { get; init; } = 5000;
    public int MaxEventPayloadBytes { get; init; } = 262_144;
    public int TailPollIntervalMilliseconds { get; init; } = 1000;
    public int SubscriberBufferSize { get; init; } = 1000;

    /// <summary>SEC-CDC99-02: GraphQL <c>fromOffset</c> accepts only events produced within this window.</summary>
    public int GraphQlResumeWindowSeconds { get; init; } = 300;

    /// <summary>SEC-CDC99-17: concurrent GraphQL catch-up readers per node.</summary>
    public int MaxConcurrentCatchUps { get; init; } = 8;

    public CdcStreamEncryptionOptions Encryption { get; init; } = new();
    public CdcStreamConsumersOptions Consumers { get; init; } = new();
}

public sealed class CdcStreamEncryptionOptions
{
    /// <summary>Must be one of <see cref="Keks"/> when set.</summary>
    public string? ActiveKekId { get; init; }

    public List<CdcKekReference> Keks { get; init; } = [];
    public int DekRotationHours { get; init; } = 24;

    /// <summary>SEC-CDC99-15: nonce budget per payload DEK (default 2^24).</summary>
    public long MaxEventsPerDek { get; init; } = 1L << 24;

    public int MaxCachedDeks { get; init; } = 256;
}

public sealed class CdcKekReference
{
    public string Id { get; init; } = string.Empty;

    /// <summary>Secret name resolved through the key vault provider; never the secret itself.</summary>
    public string SecretRef { get; init; } = string.Empty;
}

public sealed class CdcStreamConsumersOptions
{
    public int MaxConsumersPerStream { get; init; } = 100;
    public int MaxInFlight { get; init; } = 100;
    public int MaxAttempts { get; init; } = 5;
    public int MaxConsecutiveFailures { get; init; } = 20;
    public int HandlerTimeoutSeconds { get; init; } = 30;
    public int MaxAbandonedHandlers { get; init; } = 2;

    /// <summary>SEC-CDC99-07: maximum age of a captured webhook consumer identity (1..365; 0 only in Development).</summary>
    public int IdentityMaxAgeDays { get; init; } = 30;
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
