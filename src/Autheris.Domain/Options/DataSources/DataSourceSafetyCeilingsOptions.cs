namespace Autheris.Domain.Options.DataSources;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// SEC-DS05-01 / SEC-DS05-02: Global safety ceilings enforced across all data sources.
/// Per-data-source overrides cannot exceed these bounds unless dual administrative opt-in is enabled.
/// </summary>
public sealed class DataSourceSafetyCeilingsOptions
{
    /// <summary>Upper bound on query AST complexity score.</summary>
    [Range(1, 100_000)]
    public int MaxAllowedComplexityCeiling { get; init; } = 5000;

    /// <summary>Upper bound on aggregate row budget.</summary>
    [Range(1, 100_000_000)]
    public long MaxAggregateRowBudgetCeiling { get; init; } = 1_000_000;

    /// <summary>Upper bound on query execution timeout in seconds.</summary>
    [Range(1, 3600)]
    public int QueryTimeoutCeilingSeconds { get; init; } = 300;

    /// <summary>Upper bound on maximum response rows returned to clients.</summary>
    [Range(1, 100_000_000)]
    public long MaxResponseRowsCeiling { get; init; } = 100_000;

    /// <summary>Upper bound on maximum response payload size in bytes (default 50 MB).</summary>
    [Range(1024, 1_073_741_824)]
    public long MaxResponseBytesCeiling { get; init; } = 52_428_800;

    /// <summary>Upper bound on command timeout in seconds.</summary>
    [Range(1, 3600)]
    public int CommandTimeoutCeilingSeconds { get; init; } = 300;

    /// <summary>Upper bound on staging / cached table TTL in seconds (default 24 hours).</summary>
    [Range(1, 604_800)]
    public int StagingTtlCeilingSeconds { get; init; } = 86_400;

    /// <summary>Upper bound on dynamic key-set import limit for cross-source pushdowns.</summary>
    [Range(1, 1_000_000)]
    public int MaxKeySetImportLimitCeiling { get; init; } = 50_000;

    /// <summary>
    /// Master administrative opt-in required to allow any data source to exceed safety ceilings (SEC-DS05-02).
    /// Both this and <see cref="DataSourceProfileOptions.AllowCeilingRelaxation"/> must be true to bypass ceilings.
    /// </summary>
    public bool AllowGlobalCeilingRelaxation { get; init; } = false;
}
