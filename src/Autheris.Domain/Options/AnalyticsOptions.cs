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
    public const int DefaultMaxOffset = 10_000;
    public const int UpperBoundMaxOffset = 10_000_000;

    /// <summary>
    /// Largest OFFSET a single-source SQL table accepts (default 10 000, equal to the GraphQL cap; range 1-10 000 000). OFFSET cost on a database is
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

    /// <summary>Maximum number of GROUP BY columns permitted in a single aggregation query.</summary>
    public int MaxGroupByColumns { get; set; } = DefaultMaxGroupByColumns;

    /// <summary>Maximum number of aggregate expressions permitted in a single aggregation query.</summary>
    public int MaxAggregates { get; set; } = DefaultMaxAggregates;

    /// <summary>Maximum number of result groups returned by a single aggregation query.</summary>
    public int MaxGroups { get; set; } = DefaultMaxGroups;

    /// <summary>Cost weight applied per aggregate field during query cost analysis.</summary>
    public double AggregateFieldCostWeight { get; set; } = DefaultAggregateFieldCostWeight;
}

/// <summary>Minimum group size and differencing-protection settings (<c>Analytics:Privacy</c>).</summary>
public sealed class AnalyticsPrivacyOptions
{
    /// <summary>Default minimum group size (k-anonymity threshold) applied when no dataset override is configured.</summary>
    public int DefaultMinGroupSize { get; set; } = AnalyticsOptions.DefaultMinimumGroupSize;

    /// <summary>Explicit opt-in allowing small-group suppression to be disabled via dataset overrides.</summary>
    public bool AllowDisabled { get; set; }

    /// <summary>Explicit opt-in allowing catalog metadata to lower the effective minimum group size below the global default.</summary>
    public bool AllowCatalogLowering { get; set; }

    /// <summary>Explicit opt-in allowing source-declared metadata to lower the effective minimum group size.</summary>
    public bool AllowSourceDeclaredLowering { get; set; }

    /// <summary>Hard floor for catalog-supplied minimum group size overrides when lowering is permitted.</summary>
    public int CatalogMinGroupSizeFloor { get; set; } = 1;

    /// <summary>Strategy applied when a result group has fewer rows than the minimum group size.</summary>
    public SmallGroupStrategy SuppressionMode { get; set; } = SmallGroupStrategy.Suppress;

    /// <summary>
    /// MISC-09: when true and the effective minimum group size is above 1, a WHERE predicate that negates (<c>&lt;&gt;</c>, <c>NOT IN</c>,
    /// <c>NOT LIKE</c>, <c>NOT BETWEEN</c>, <c>IS NOT</c> other than <c>IS NOT NULL</c>, <c>NOT (...)</c>) or isolates one row (equality on a unique
    /// or primary key column) on a column that is not a group-by column is rejected.
    /// </summary>
    public bool RestrictNegatedFilters { get; set; } = true;

    /// <summary>When true, includes the count of suppressed groups in aggregation response metadata.</summary>
    public bool ExposeSuppressedGroupsCount { get; set; } = false;

    /// <summary>Per-dataset minimum group size and suppression mode overrides keyed by dataset identifier.</summary>
    public Dictionary<string, DatasetPrivacyOverrideOptions> Datasets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Per-dataset override for minimum group size and suppression mode.</summary>
public sealed class DatasetPrivacyOverrideOptions
{
    /// <summary>Optional minimum group size override for this dataset.</summary>
    public int? MinGroupSize { get; set; }

    /// <summary>Optional suppression strategy override for this dataset.</summary>
    public SmallGroupStrategy? SuppressionMode { get; set; }
}


