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
    SmallGroupStrategy? SmallGroupStrategyOverride = null);

/// <summary>
/// Governed execution result for a semantic metric query, including manifest version provenance and k-anonymity metadata (#38).
/// </summary>
public sealed record MetricQueryResult(
    IReadOnlyList<string> Metrics,
    string ManifestVersion,
    IReadOnlyList<AggregationGroupRow> Groups,
    AggregationGroupRow? Totals,
    AggregationMetadata Metadata,
    IReadOnlyDictionary<string, DbtMetric> MetricDefinitions)
{
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
