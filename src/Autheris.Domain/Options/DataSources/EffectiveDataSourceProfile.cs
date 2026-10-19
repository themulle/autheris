namespace Autheris.Domain.Options.DataSources;

using System;
using System.Data;

/// <summary>
/// Fully resolved, immutable runtime profile for a data source (BL-05).
/// All hierarchical overrides, fallback defaults, and safety ceilings have been pre-computed.
/// Lookups are zero-allocation O(1) in steady state.
/// </summary>
public sealed record EffectiveDataSourceProfile(
    string DataSourceName,
    // 1. Cost & Quota Settings
    double QueryCostMultiplier,
    int MaxAllowedComplexity,
    long MaxAggregateRowBudget,
    TimeSpan QueryTimeout,
    // 2. Logging & Telemetry Settings
    string MinimumLogLevel,
    bool LogGeneratedSql,
    double SamplingRatio,
    OutboundPropagationMode OutboundPropagation,
    // 3. Operational Guardrails
    long MaxResponseRows,
    long MaxResponseBytes,
    TimeSpan CommandTimeout,
    int? MinPoolSize,
    int? MaxPoolSize,
    IsolationLevel DefaultIsolationLevel,
    DataSourceTransportFlags AllowedTransports,
    // 4. Caching & Staging Guardrails
    bool CachingEnabled,
    TimeSpan StagingTtl,
    int MaxKeySetImportLimit,
    // 5. Anti-Escalation State
    bool CeilingRelaxed
)
{
    /// <summary>Checks whether the specified ingress transport is authorized for this data source.</summary>
    public bool IsTransportAllowed(DataSourceTransportFlags transport) =>
        (AllowedTransports & transport) == transport;
}
