namespace Autheris.Domain.Options.DataSources;

using System;
using System.Collections.Generic;

/// <summary>
/// Root configuration container for per-data-source options and safety ceilings (BL-05).
/// </summary>
public sealed class DataSourceConfigurationOptions
{
    public const string SectionName = "Gateway:DataSources:Configuration";

    /// <summary>Default profile options used when a specific data source does not specify an override.</summary>
    public DataSourceProfileOptions Defaults { get; init; } = new();

    /// <summary>Global safety ceilings enforcing non-escalation guardrails across all sources (SEC-DS05-01).</summary>
    public DataSourceSafetyCeilingsOptions Ceilings { get; init; } = new();

    /// <summary>Per-data-source profile overrides keyed by data source name (case-insensitive).</summary>
    public Dictionary<string, DataSourceProfileOptions> Profiles { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
