namespace Autheris.Domain.Options.DataSources;

using System.ComponentModel.DataAnnotations;
using System.Data;

/// <summary>
/// Granular configuration overrides for a specific data source (BL-05).
/// Nullable properties cleanly inherit from global gateway defaults when left unspecified.
/// </summary>
public sealed class DataSourceProfileOptions
{
    // ==========================================
    // 1. Cost & Quota Settings
    // ==========================================

    /// <summary>Cost multiplier applied to query cost calculations for this data source.</summary>
    [Range(0.01, 1000.0)]
    public double? QueryCostMultiplier { get; init; }

    /// <summary>Maximum query AST complexity permitted for queries targeting this source.</summary>
    [Range(1, 100_000)]
    public int? MaxAllowedComplexity { get; init; }

    /// <summary>Maximum aggregate rows this source may scan/aggregate in a single query.</summary>
    [Range(1, 100_000_000)]
    public long? MaxAggregateRowBudget { get; init; }

    /// <summary>Execution timeout in seconds for queries directed to this data source.</summary>
    [Range(1, 3600)]
    public int? QueryTimeoutSeconds { get; init; }

    // ==========================================
    // 2. Logging & Telemetry Settings
    // ==========================================

    /// <summary>Minimum log level override for operations targeting this data source.</summary>
    public string? MinimumLogLevel { get; init; }

    /// <summary>Whether generated and compiled SQL statements targeting this data source may be logged.</summary>
    public bool? LogGeneratedSql { get; init; }

    /// <summary>Trace sampling ratio override (0.0 to 1.0) for queries on this data source.</summary>
    [Range(0.0, 1.0)]
    public double? SamplingRatio { get; init; }

    /// <summary>Outbound W3C TraceContext propagation policy for this data source (SEC-DS05-07).</summary>
    public OutboundPropagationMode OutboundPropagation { get; init; } = OutboundPropagationMode.Inherit;

    // ==========================================
    // 3. Operational & Data Guardrails
    // ==========================================

    /// <summary>Hard limit on the number of rows returned in a single query result set.</summary>
    [Range(1, 100_000_000)]
    public long? MaxResponseRows { get; init; }

    /// <summary>Hard limit on response buffer size in bytes.</summary>
    [Range(1024, 1_073_741_824)]
    public long? MaxResponseBytes { get; init; }

    /// <summary>ADO.NET / statement command execution timeout in seconds.</summary>
    [Range(1, 3600)]
    public int? CommandTimeoutSeconds { get; init; }

    /// <summary>Minimum connection pool size.</summary>
    [Range(0, 10_000)]
    public int? MinPoolSize { get; init; }

    /// <summary>Maximum connection pool size.</summary>
    [Range(1, 10_000)]
    public int? MaxPoolSize { get; init; }

    /// <summary>Default transaction isolation level for sessions opened against this data source.</summary>
    public IsolationLevel? DefaultIsolationLevel { get; init; }

    /// <summary>Permitted ingress transports authorized to query this data source (SEC-DS05-03).</summary>
    public DataSourceTransportFlags AllowedTransports { get; init; } = DataSourceTransportFlags.All;

    // ==========================================
    // 4. Caching & Staging Guardrails
    // ==========================================

    /// <summary>Enables or disables result and key-set caching for queries targeting this source.</summary>
    public bool? CachingEnabled { get; init; }

    /// <summary>Time-to-live in seconds for staging tables or cached results from this data source.</summary>
    [Range(1, 604_800)]
    public int? StagingTtlSeconds { get; init; }

    /// <summary>Maximum key-set import limit for cross-source pushdowns to this data source.</summary>
    [Range(1, 1_000_000)]
    public int? MaxKeySetImportLimit { get; init; }

    // ==========================================
    // 5. Anti-Escalation & Security Override
    // ==========================================

    /// <summary>
    /// Explicit per-source opt-in to relax safety ceilings (SEC-DS05-02).
    /// Requires <see cref="DataSourceSafetyCeilingsOptions.AllowGlobalCeilingRelaxation"/> to be true.
    /// </summary>
    public bool AllowCeilingRelaxation { get; init; } = false;
}
