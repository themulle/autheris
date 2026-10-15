namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// Structured semantics for a dataset column (e.g. from dbt meta, schema annotations, or business glossaries).
/// </summary>
public sealed record ColumnSemantics(
    string? Role = null,        // e.g. "measure", "dimension", "time_dimension", "entity"
    string? Unit = null,        // e.g. "EUR", "USD", "ms", "count", "pct"
    string? BusinessName = null,
    IReadOnlyDictionary<string, string>? RawMeta = null
);

/// <summary>
/// Metric representation from dbt Semantic Layer (semantic_manifest.json).
/// </summary>
public sealed record DbtMetric(
    string Name,
    string Type, // "simple", "derived", "cumulative", "ratio"
    string? Description,
    string? Label,
    string? Filter,
    string? TypeParams,
    IReadOnlyList<string> Dimensions,
    IReadOnlyList<string> Measures,
    IReadOnlyDictionary<string, string> Meta,
    string? Unit = null,
    string? Format = null,
    int? MinGroupSize = null,
    IReadOnlyList<string>? AllowedTimeGranularities = null,
    string? Version = null
);

/// <summary>
/// Measure representation from dbt Semantic Layer.
/// </summary>
public sealed record DbtMeasure(
    string Name,
    string Aggregation, // "sum", "count", "avg", "min", "max", "count_distinct"
    string? Expr,
    string? Description,
    string? Unit = null,
    int? MinGroupSize = null
);

/// <summary>
/// Dimension representation from dbt Semantic Layer.
/// </summary>
public sealed record DbtDimension(
    string Name,
    string Type, // "categorical", "time"
    string? Description,
    bool IsPartition = false,
    string? TypeParams = null,
    string? TimeGranularity = null,
    IReadOnlyList<string>? AllowedTimeGranularities = null
);

/// <summary>
/// Entity representation from dbt Semantic Layer.
/// </summary>
public sealed record DbtEntity(
    string Name,
    string Type, // "primary", "unique", "foreign"
    string? Role = null
);

/// <summary>
/// Semantic model grouping dimensions, measures, and entities for a table.
/// </summary>
public sealed record DbtSemanticModel(
    string Name,
    string Description,
    string NodeRelation,
    IReadOnlyList<DbtEntity> Entities,
    IReadOnlyList<DbtMeasure> Measures,
    IReadOnlyList<DbtDimension> Dimensions
);

/// <summary>
/// Parsed dbt Semantic Manifest (semantic_manifest.json).
/// </summary>
public sealed record DbtSemanticManifest(
    IReadOnlyList<DbtSemanticModel> SemanticModels,
    IReadOnlyList<DbtMetric> Metrics,
    string Version = "1.0"
);

