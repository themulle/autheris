namespace Autheris.Application.Analytics;

using System;
using System.Collections.Generic;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Supported SQL/GraphQL aggregate functions for governed aggregation (#28).
/// </summary>
public enum AggregateFunction
{
    Count = 0,
    Sum = 1,
    Avg = 2,
    Min = 3,
    Max = 4,
    CountDistinct = 5
}

/// <summary>
/// Single aggregate projection requested on a table column (or <c>COUNT(*)</c> when <see cref="Column"/> is null).
/// </summary>
public sealed record AggregateSelection(
    AggregateFunction Function,
    string? Column = null,
    string? Alias = null)
{
    public string EffectiveAlias =>
        !string.IsNullOrWhiteSpace(Alias)
            ? Alias!
            : Column is null
                ? "count"
                : $"{Function.ToString().ToLowerInvariant()}_{Column}";
}

/// <summary>
/// Transport-neutral governed aggregation request (#28, #37).
/// </summary>
public sealed record AggregationQuery(
    TableIdentifier Table,
    IReadOnlyList<string>? GroupBy = null,
    IReadOnlyList<AggregateSelection>? Aggregates = null,
    TableFilterClause? Where = null,
    IReadOnlyList<TableOrderBy>? OrderBy = null,
    int? Limit = null,
    int? MinGroupSizeOverride = null,
    SmallGroupStrategy? SmallGroupStrategyOverride = null,
    string? Transport = null,
    bool BypassTableConsent = false,
    TableAccessDecision? PreEvaluatedDecision = null);

/// <summary>
/// A single aggregated bucket (or grand total row) after minimum group size enforcement (#28, #37).
/// </summary>
public sealed record AggregationGroupRow(
    IReadOnlyDictionary<string, object?> GroupKey,
    long Count,
    IReadOnlyDictionary<string, object?> Aggregates,
    bool IsMergedOther = false)
{
    public IReadOnlyDictionary<string, object?> Group => GroupKey;
    public IReadOnlyDictionary<string, object?> Values => Aggregates;
    public IReadOnlyDictionary<string, object?> Sum { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, object?> Avg { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, object?> Min { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, object?> Max { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Privacy and execution metadata accompanying an <see cref="AggregationResult"/> (#37).
/// </summary>
public sealed record AggregationMetadata(
    int MinimumGroupSize,
    SmallGroupStrategy Strategy,
    bool Suppressed,
    int SuppressedGroupsCount,
    long SuppressedRowsCount,
    bool MergedIntoOther,
    bool Truncated,
    bool NoiseApplied = false,
    double? EpsilonConsumed = null,
    double? RemainingEpsilon = null);

/// <summary>
/// Governed aggregation result with groups, differencing-safe totals, and privacy metadata (#28, #37).
/// </summary>
public sealed record AggregationResult(
    TableIdentifier Table,
    IReadOnlyList<AggregationGroupRow> Groups,
    AggregationGroupRow? Totals,
    AggregationMetadata Metadata)
{
    public bool Suppressed => Metadata.Suppressed;
    public int SuppressedGroupsCount => Metadata.SuppressedGroupsCount;
    public long SuppressedRowsCount => Metadata.SuppressedRowsCount;
    public int MinimumGroupSize => Metadata.MinimumGroupSize;
    public bool MergedIntoOther => Metadata.MergedIntoOther;
    public bool Truncated => Metadata.Truncated;
    public bool NoiseApplied => Metadata.NoiseApplied;
}

/// <summary>
/// Thrown when a caller attempts to group by or aggregate a column that is masked for their principal (#28).
/// </summary>
public sealed class MaskedAggregationException : GatewaySecurityException
{
    public const string ErrorCodeConst = "AGGREGATION_ON_MASKED_COLUMN";
    public const string FixedMessage = "The requested column is masked and cannot be used in an aggregation query.";
    public string ColumnName { get; }

    public MaskedAggregationException(string columnName, string? message = null)
        : base(
            message ?? FixedMessage,
            ErrorCodeConst)
    {
        ColumnName = columnName;
    }
}

/// <summary>
/// Thrown when a column used in <c>groupBy</c>, an aggregate argument, <c>where</c> or <c>orderBy</c> is unknown or denied for the caller
/// (<c>AGGREGATION_COLUMN_UNAVAILABLE</c>). Unknown and denied columns are deliberately indistinguishable (type, code, message, audit text),
/// and the message never carries the requested name, so the error cannot be used as an existence oracle for columns the caller may not read.
/// </summary>
public sealed class AggregationColumnUnavailableException : GatewaySecurityException
{
    public const string ErrorCodeConst = "AGGREGATION_COLUMN_UNAVAILABLE";
    public const string FixedMessage = "The requested column is not available for aggregation.";

    public AggregationColumnUnavailableException()
        : base(FixedMessage, ErrorCodeConst)
    {
    }
}

/// <summary>
/// Thrown when an aggregation would have to read at least the in-memory row cap: the aggregate over a truncated prefix would be wrong,
/// so the call fails closed (<c>AGGREGATION_ROW_CAP_EXCEEDED</c>) instead of returning a silently truncated result.
/// </summary>
public sealed class AggregationRowCapExceededException : GatewaySecurityException
{
    public const string ErrorCodeConst = "AGGREGATION_ROW_CAP_EXCEEDED";
    public const string FixedMessage = "The aggregation covers more rows than the governed in-memory aggregation limit allows; narrow the filter.";

    public AggregationRowCapExceededException()
        : base(FixedMessage, ErrorCodeConst)
    {
    }
}
