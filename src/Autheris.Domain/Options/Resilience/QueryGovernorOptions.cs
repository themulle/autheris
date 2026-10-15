using System.ComponentModel.DataAnnotations;

namespace Autheris.Domain.Options;

public sealed class ResourceGroupsOptions
{
    public bool Enabled { get; init; } = true;
    public ResourceGroupTierConfigOptions Interactive { get; init; } = new(50, 20, 5);
    public ResourceGroupTierConfigOptions AutonomousAgents { get; init; } = new(10, 50, 15);
    public ResourceGroupTierConfigOptions BulkAnalytics { get; init; } = new(5, 100, 60);

    /// <summary>
    /// SEC H-07: Long-lived connections (WebSocket upgrades, SSE) do not occupy resource group slots;
    /// instead they are capped per principal (SID) and per tenant.
    /// </summary>
    [Range(1, 10000)] public int MaxPersistentConnectionsPerPrincipal { get; init; } = 5;
    [Range(1, 100000)] public int MaxPersistentConnectionsPerTenant { get; init; } = 50;

    /// <summary>
    /// SEC H-07: Share of a tier's MaxConcurrency a single tenant may hold at once (percent, min. 1 slot).
    /// The tenant's queue share is derived proportionally from MaxQueueDepth.
    /// </summary>
    [Range(1, 100)] public int MaxConcurrentPerTenantPercent { get; init; } = 50;

    /// <summary>
    /// SEC H-07: Absolute per-tenant concurrency limit per tier (capped at the tier's MaxConcurrency).
    /// 0 = use <see cref="MaxConcurrentPerTenantPercent"/>.
    /// </summary>
    [Range(0, 1000)] public int MaxConcurrentPerTenant { get; init; } = 0;
}

public sealed record ResourceGroupTierConfigOptions(
    [Range(1, 1000)] int MaxConcurrency,
    [Range(0, 10000)] int MaxQueueDepth,
    [Range(1, 600)] int TimeoutSeconds
);

/// <summary>
/// Row limits per SQL transport. Every channel runs the governed WebSQL pipeline; an unset value falls back to
/// WebSql.DefaultMaxRows / WebSql.MaxAllowedRows (the limits of POST /api/v1/sql).
/// </summary>
public sealed class TransportRowLimitsOptions
{
    /// <summary>Trino client protocol (POST /v1/statement).</summary>
    public ChannelRowLimitOptions Trino { get; init; } = new();

    /// <summary>WebSQL with Accept: application/vnd.apache.parquet (ParquetEgress.MaxRowsPerFile still caps the file).</summary>
    public ChannelRowLimitOptions Parquet { get; init; } = new();

    /// <summary>Declared SQL endpoints (/api/v1/queries/...).</summary>
    public ChannelRowLimitOptions SqlEndpoints { get; init; } = new();

    /// <summary>Arrow IPC export (POST /api/v1/export/arrow; Arrow.MaxExportRows still caps the export).</summary>
    public ChannelRowLimitOptions ArrowExport { get; init; } = new();

    /// <summary>Arrow Flight SQL (/api/v1/flight/sql/*).</summary>
    public ChannelRowLimitOptions FlightSql { get; init; } = new();

    /// <summary>Asynchronous query jobs (POST /api/v1/jobs/query); the client MaxRows is clamped to MaxAllowedRows (SR-H-05).</summary>
    public ChannelRowLimitOptions AsyncJob { get; init; } = new();
}

/// <summary>Row limits of one transport; null keeps the WebSql value.</summary>
public sealed class ChannelRowLimitOptions
{
    /// <summary>Rows returned when the statement has no LIMIT.</summary>
    public long? DefaultMaxRows { get; init; }

    /// <summary>Upper bound for an explicit LIMIT (0 = no bound).</summary>
    public long? MaxAllowedRows { get; init; }
}
