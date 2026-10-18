namespace Autheris.Application.Analytics;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Request to execute one or more governed dbt Semantic Layer metrics (#38).
/// </summary>
public sealed record MetricQueryRequest(
    IReadOnlyList<string> Metrics,
    IReadOnlyList<string>? GroupBy = null,
    TableFilterClause? Where = null,
    string? TimeGranularity = null,
    IReadOnlyList<TableOrderBy>? OrderBy = null,
    int? Limit = null,
    SmallGroupStrategy? SmallGroupStrategyOverride = null)
{
    /// <summary>
    /// Typed filter of the GraphQL surface (#112 A-8). Built into a clause after the base table is resolved; mutually exclusive with
    /// <see cref="Where"/> (REST and MCP keep the text filter).
    /// </summary>
    public Autheris.Application.Sql.Tree.TreeFilter? TypedWhere { get; init; }
}

/// <summary>
/// Governed execution result for a semantic metric query, including manifest version provenance and k-anonymity metadata (#38).
/// </summary>
public sealed record MetricQueryResult(
    IReadOnlyList<string> Metrics,
    string ManifestVersion,
    IReadOnlyList<AggregationGroupRow> Groups,
    AggregationGroupRow? Totals,
    AggregationMetadata Metadata,
    IReadOnlyDictionary<string, Autheris.Application.Dbt.Services.MetricDefinitionSummary> MetricDefinitions)
{
    /// <summary>Static value kind per requested metric (count INT, ratio and derived DECIMAL, otherwise from the function and column type). For the typed surface only: REST and MCP output is unchanged.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, AggregateValueKind> MetricValueKinds { get; init; } = new Dictionary<string, AggregateValueKind>();

    /// <summary>Static value kind per group-by column.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, AggregateValueKind> DimensionValueKinds { get; init; } = new Dictionary<string, AggregateValueKind>();

    public bool Suppressed => Metadata.Suppressed;
    public int SuppressedGroupsCount => Metadata.SuppressedGroupsCount;
    public long SuppressedRowsCount => Metadata.SuppressedRowsCount;
    public int MinimumGroupSize => Metadata.MinimumGroupSize;
    public bool MergedIntoOther => Metadata.MergedIntoOther;
}

/// <summary>
/// Compiles and executes dbt Semantic Layer metric queries through the governed aggregation pipeline (#38).
/// </summary>
public interface ISemanticMetricQueryService
{
    Task<MetricQueryResult> QueryMetricsAsync(
        ClaimsPrincipal principal,
        MetricQueryRequest request,
        CancellationToken ct = default);
}
