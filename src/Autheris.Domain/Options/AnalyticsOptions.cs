namespace Autheris.Domain.Options;

using System;
using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>Options bound to <c>Analytics</c>.</summary>
public sealed class AnalyticsOptions
{
    public const string SectionName = "Analytics";
    public const int DefaultMinimumGroupSize = 5;

    /// <summary>Settings of the governed REST dataset API (<c>Analytics:DatasetApi</c>).</summary>
    public DatasetApiOptions DatasetApi { get; set; } = new();

    /// <summary>Settings for governed GraphQL/REST/MCP aggregations (<c>Analytics:Aggregation</c>).</summary>
    public AggregationOptions Aggregation { get; set; } = new();

    /// <summary>Minimum group size and k-anonymity guard settings (<c>Analytics:Privacy</c>).</summary>
    public AnalyticsPrivacyOptions Privacy { get; set; } = new();

    /// <summary>
    /// Top-level convenience alias for <see cref="AnalyticsPrivacyOptions.DefaultMinGroupSize"/>.
    /// </summary>
    public int MinimumGroupSize
    {
        get => Privacy.DefaultMinGroupSize;
        set => Privacy.DefaultMinGroupSize = value;
    }

    /// <summary>
    /// Top-level convenience alias for <see cref="AnalyticsPrivacyOptions.SuppressionMode"/>.
    /// </summary>
    public SmallGroupStrategy SmallGroupStrategy
    {
        get => Privacy.SuppressionMode;
        set => Privacy.SuppressionMode = value;
    }

    /// <summary>
    /// Per-table minimum group size overrides keyed by table name or qualified name (<c>schema.table</c> or <c>domain.schema.table</c>).
    /// </summary>
    public Dictionary<string, int> TableMinimumGroupSizes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Governed REST dataset API settings.</summary>
public sealed class DatasetApiOptions
{
    public const int DefaultMaxOffset = 100_000;
    public const int UpperBoundMaxOffset = 10_000_000;

    /// <summary>
    /// Largest OFFSET a single-source SQL table accepts (default 100 000, range 0-10 000 000). OFFSET cost on a database is
    /// still linear, so an explicit ceiling prevents full-table scans through the REST API.
    /// </summary>
    public int MaxOffset { get; set; } = DefaultMaxOffset;
}

/// <summary>Governed aggregation execution bounds and cost settings (<c>Analytics:Aggregation</c>).</summary>
public sealed class AggregationOptions
{
    public const int DefaultMaxGroupByColumns = 4;
    public const int DefaultMaxAggregates = 16;
    public const int DefaultMaxGroups = 1_000;
    public const int UpperBoundMaxGroups = 50_000;
    public const double DefaultAggregateFieldCostWeight = 25.0;

    public bool Enabled { get; set; } = true;
    public int MaxGroupByColumns { get; set; } = DefaultMaxGroupByColumns;
    public int MaxAggregates { get; set; } = DefaultMaxAggregates;
    public int MaxGroups { get; set; } = DefaultMaxGroups;
    public double AggregateFieldCostWeight { get; set; } = DefaultAggregateFieldCostWeight;
}

/// <summary>Minimum group size and differencing-protection settings (<c>Analytics:Privacy</c>).</summary>
public sealed class AnalyticsPrivacyOptions
{
    public int DefaultMinGroupSize { get; set; } = AnalyticsOptions.DefaultMinimumGroupSize;
    public bool AllowDisabled { get; set; }
    public SmallGroupStrategy SuppressionMode { get; set; } = SmallGroupStrategy.Suppress;
    public bool RestrictNegatedFilters { get; set; } = true;
    public bool ExposeSuppressedGroupsCount { get; set; } = true;
    public Dictionary<string, DatasetPrivacyOverrideOptions> Datasets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public AnalyticsNoiseOptions Noise { get; set; } = new();
}

/// <summary>Per-dataset override for minimum group size and suppression mode.</summary>
public sealed class DatasetPrivacyOverrideOptions
{
    public int? MinGroupSize { get; set; }
    public SmallGroupStrategy? SuppressionMode { get; set; }
}

/// <summary>Differential privacy Laplace noise settings for governed aggregations (<c>Analytics:Privacy:Noise</c>).</summary>
public sealed class AnalyticsNoiseOptions
{
    public bool Enabled { get; set; }
    public double Epsilon { get; set; } = 1.0;
    public double DailyBudgetEpsilon { get; set; } = 10.0;
    public int CacheMaxEntries { get; set; } = 10_000;
    public Dictionary<string, MeasureBoundsOptions> MeasureBounds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Clipping / sensitivity bounds for a numeric measure column (<c>Max &gt; Min</c>).</summary>
public sealed class MeasureBoundsOptions
{
    public double Min { get; set; }
    public double Max { get; set; }
}


