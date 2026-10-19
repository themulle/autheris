namespace Autheris.Application.Analytics;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Data;
using Autheris.Application.Data.Services;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

public sealed partial class GovernedAggregationService
{
    private static List<ComputedBucket> BuildBucketsFromRows(
        List<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<string> groupByColumns,
        IReadOnlyList<AggregateSelection> aggregates,
        TableMetadata metadata,
        IReadOnlyList<TimeBucketSpecification>? timeBuckets = null)
    {
        if (groupByColumns.Count == 0)
        {
            if (rows.Count == 0)
            {
                return [];
            }

            var emptyKey = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            return [ComputeBucketFromRows(emptyKey, rows, aggregates, metadata, isMergedOther: false)];
        }

        var bucketMap = timeBuckets?.ToDictionary(b => b.Column, b => b, StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, (Dictionary<string, object?> Key, List<IReadOnlyDictionary<string, object?>> Rows)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var keyDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var compositeParts = new string[groupByColumns.Count];
            for (int i = 0; i < groupByColumns.Count; i++)
            {
                var col = groupByColumns[i];
                var val = GetRowValue(row, col);
                if (bucketMap != null && bucketMap.TryGetValue(col, out var spec))
                {
                    val = TruncateValueToTimeBucket(val, spec);
                }
                keyDict[col] = val;
                compositeParts[i] = val is null or DBNull
                    ? "\u0000NULL\u0000"
                    : Convert.ToString(val, CultureInfo.InvariantCulture) ?? string.Empty;
            }

            var compositeKey = string.Join("\u001F", compositeParts);
            if (!groups.TryGetValue(compositeKey, out var entry))
            {
                entry = (keyDict, new List<IReadOnlyDictionary<string, object?>>());
                groups[compositeKey] = entry;
            }

            entry.Rows.Add(row);
        }

        var buckets = new List<ComputedBucket>(groups.Count);
        foreach (var (_, (keyDict, bucketRows)) in groups)
        {
            buckets.Add(ComputeBucketFromRows(keyDict, bucketRows, aggregates, metadata, isMergedOther: false));
        }

        return buckets;
    }

    private static ComputedBucket ComputeBucketFromRows(
        IReadOnlyDictionary<string, object?> groupKey,
        List<IReadOnlyDictionary<string, object?>> bucketRows,
        IReadOnlyList<AggregateSelection> aggregates,
        TableMetadata metadata,
        bool isMergedOther)
    {
        long count = bucketRows.Count;
        var aggValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var sums = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var avgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var mins = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var maxs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var agg in aggregates)
        {
            var alias = agg.EffectiveAlias;
            if (agg.Column == null)
            {
                aggValues[alias] = count;
                continue;
            }

            var col = agg.Column;
            var nonNullValues = bucketRows
                .Select(r => GetRowValue(r, col))
                .Where(v => v is not null and not DBNull)
                .ToList();

            switch (agg.Function)
            {
                case AggregateFunction.Count:
                    aggValues[alias] = (long)nonNullValues.Count;
                    break;

                case AggregateFunction.CountDistinct:
                    var distinct = new HashSet<string>(
                        nonNullValues.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty),
                        StringComparer.Ordinal);
                    aggValues[alias] = (long)distinct.Count;
                    break;

                case AggregateFunction.Sum:
                    if (nonNullValues.Count == 0)
                    {
                        aggValues[alias] = 0m;
                        sums[col] = 0m;
                    }
                    else
                    {
                        decimal sum = nonNullValues.Sum(ToDecimal);
                        object sumBoxed = PreferIntegralOrDecimal(sum, metadata.GetColumn(col)?.DataType);
                        aggValues[alias] = sumBoxed;
                        sums[col] = sumBoxed;
                    }
                    break;

                case AggregateFunction.Avg:
                    if (nonNullValues.Count == 0)
                    {
                        aggValues[alias] = null;
                        avgs[col] = null;
                    }
                    else
                    {
                        decimal avg = nonNullValues.Average(ToDecimal);
                        aggValues[alias] = avg;
                        avgs[col] = avg;
                    }
                    break;

                case AggregateFunction.Min:
                    if (nonNullValues.Count == 0)
                    {
                        aggValues[alias] = null;
                        mins[col] = null;
                    }
                    else
                    {
                        object? minVal = ComputeMinOrMax(nonNullValues, isMax: false);
                        aggValues[alias] = minVal;
                        mins[col] = minVal;
                    }
                    break;

                case AggregateFunction.Max:
                    if (nonNullValues.Count == 0)
                    {
                        aggValues[alias] = null;
                        maxs[col] = null;
                    }
                    else
                    {
                        object? maxVal = ComputeMinOrMax(nonNullValues, isMax: true);
                        aggValues[alias] = maxVal;
                        maxs[col] = maxVal;
                    }
                    break;
            }
        }

        return new ComputedBucket(groupKey, count, aggValues, sums, avgs, mins, maxs, bucketRows, isMergedOther);
    }

    private static List<ComputedBucket> BuildBucketsFromPlannedSqlResult(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> sqlRows,
        IReadOnlyList<string> groupByColumns,
        IReadOnlyList<AggregateSelection> aggregates,
        string countColumnAlias)
    {
        var buckets = new List<ComputedBucket>(sqlRows.Count);
        foreach (var row in sqlRows)
        {
            var groupKey = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var gCol in groupByColumns)
            {
                groupKey[gCol] = GetRowValue(row, gCol);
            }

            long count = Convert.ToInt64(GetRowValue(row, countColumnAlias) ?? 0L, CultureInfo.InvariantCulture);
            var aggValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var sums = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var avgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var mins = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var maxs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var agg in aggregates)
            {
                var val = GetRowValue(row, agg.EffectiveAlias);
                aggValues[agg.EffectiveAlias] = val;
                if (agg.Column != null)
                {
                    switch (agg.Function)
                    {
                        case AggregateFunction.Sum:
                            sums[agg.Column] = val;
                            break;
                        case AggregateFunction.Avg:
                            avgs[agg.Column] = val;
                            break;
                        case AggregateFunction.Min:
                            mins[agg.Column] = val;
                            break;
                        case AggregateFunction.Max:
                            maxs[agg.Column] = val;
                            break;
                    }
                }
            }

            buckets.Add(new ComputedBucket(groupKey, count, aggValues, sums, avgs, mins, maxs, [], IsMergedOther: false));
        }

        return buckets;
    }

    private static ComputedBucket MergePreAggregatedBuckets(
        IReadOnlyDictionary<string, object?> groupKey,
        IReadOnlyList<ComputedBucket> buckets,
        IReadOnlyList<AggregateSelection> aggregates,
        bool isMergedOther)
    {
        long totalCount = buckets.Sum(b => b.Count);
        var aggValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var sums = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var avgs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var mins = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var maxs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var agg in aggregates)
        {
            var alias = agg.EffectiveAlias;
            if (agg.Column == null)
            {
                aggValues[alias] = totalCount;
                continue;
            }

            var col = agg.Column;
            var nonNullVals = buckets
                .Select(b => b.Aggregates.TryGetValue(alias, out var v) ? v : null)
                .Where(v => v is not null and not DBNull)
                .ToList();

            switch (agg.Function)
            {
                case AggregateFunction.Count:
                case AggregateFunction.CountDistinct:
                    aggValues[alias] = nonNullVals.Sum(v => Convert.ToInt64(v, CultureInfo.InvariantCulture));
                    break;

                case AggregateFunction.Sum:
                    decimal sum = nonNullVals.Sum(ToDecimal);
                    aggValues[alias] = sum;
                    sums[col] = sum;
                    break;

                case AggregateFunction.Avg:
                    if (totalCount == 0 || nonNullVals.Count == 0)
                    {
                        aggValues[alias] = null;
                        avgs[col] = null;
                    }
                    else
                    {
                        decimal weightedSum = 0m;
                        long weightCount = 0;
                        foreach (var b in buckets)
                        {
                            if (b.Aggregates.TryGetValue(alias, out var v) && v is not null and not DBNull && b.Count > 0)
                            {
                                weightedSum += ToDecimal(v) * b.Count;
                                weightCount += b.Count;
                            }
                        }
                        decimal avg = weightCount > 0 ? weightedSum / weightCount : 0m;
                        aggValues[alias] = avg;
                        avgs[col] = avg;
                    }
                    break;

                case AggregateFunction.Min:
                    var minVal = nonNullVals.Count > 0 ? ComputeMinOrMax(nonNullVals!, isMax: false) : null;
                    aggValues[alias] = minVal;
                    mins[col] = minVal;
                    break;

                case AggregateFunction.Max:
                    var maxVal = nonNullVals.Count > 0 ? ComputeMinOrMax(nonNullVals!, isMax: true) : null;
                    aggValues[alias] = maxVal;
                    maxs[col] = maxVal;
                    break;
            }
        }

        return new ComputedBucket(groupKey, totalCount, aggValues, sums, avgs, mins, maxs, [], isMergedOther);
    }

    private static AggregationGroupRow ComputeDifferencingSafeTotals(
        IReadOnlyList<ComputedBucket> survivingBuckets,
        IReadOnlyList<AggregateSelection> aggregates,
        TableMetadata metadata)
    {
        var totals = ComputeDifferencingSafeTotalsCore(survivingBuckets, aggregates, metadata);

        // WP-0 (SEC-112-07): when a merged OTHER row is part of the visible set, the totals must not expose the extremes, the distinct count
        // or the average of the merged small groups either (a size-1 group can be exactly the global minimum or maximum).
        return survivingBuckets.Any(b => b.IsMergedOther) ? WithoutNonMergeableValues(totals, aggregates) : totals;
    }

    private static bool IsNonMergeable(AggregateFunction function) =>
        function is AggregateFunction.Min or AggregateFunction.Max or AggregateFunction.CountDistinct or AggregateFunction.Avg;

    /// <summary>
    /// A merged <c>OTHER</c> row is built from groups below the minimum size. <c>MIN</c>, <c>MAX</c>, <c>COUNT_DISTINCT</c> and <c>AVG</c> of such a union
    /// are not safe to publish (the extreme of the union can be the value of a single suppressed row), so they are returned as <c>null</c>.
    /// The raw rows stay on the bucket for the differencing-safe totals.
    /// </summary>
    private static ComputedBucket WithoutNonMergeableValues(ComputedBucket bucket, IReadOnlyList<AggregateSelection> aggregates) =>
        bucket with { Aggregates = NullNonMergeable(bucket.Aggregates, aggregates), Avg = EmptyValues(), Min = EmptyValues(), Max = EmptyValues() };

    private static AggregationGroupRow WithoutNonMergeableValues(AggregationGroupRow row, IReadOnlyList<AggregateSelection> aggregates) =>
        row with { Aggregates = NullNonMergeable(row.Aggregates, aggregates), Avg = EmptyValues(), Min = EmptyValues(), Max = EmptyValues() };

    private static Dictionary<string, object?> EmptyValues() => new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, object?> NullNonMergeable(IReadOnlyDictionary<string, object?> source, IReadOnlyList<AggregateSelection> aggregates)
    {
        var values = new Dictionary<string, object?>(source, StringComparer.OrdinalIgnoreCase);
        foreach (var agg in aggregates.Where(a => IsNonMergeable(a.Function)))
        {
            values[agg.EffectiveAlias] = null;
        }

        return values;
    }

    private static AggregationGroupRow ComputeDifferencingSafeTotalsCore(
        IReadOnlyList<ComputedBucket> survivingBuckets,
        IReadOnlyList<AggregateSelection> aggregates,
        TableMetadata metadata)
    {
        var emptyKey = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (survivingBuckets.Count == 0)
        {
            var zeroAggs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var zeroSums = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var agg in aggregates)
            {
                if (agg.Function is AggregateFunction.Count or AggregateFunction.CountDistinct)
                {
                    zeroAggs[agg.EffectiveAlias] = 0L;
                }
                else if (agg.Function == AggregateFunction.Sum)
                {
                    zeroAggs[agg.EffectiveAlias] = 0m;
                    if (agg.Column != null)
                    {
                        zeroSums[agg.Column] = 0m;
                    }
                }
                else
                {
                    zeroAggs[agg.EffectiveAlias] = null;
                }
            }

            return new AggregationGroupRow(emptyKey, 0L, zeroAggs)
            {
                Sum = zeroSums
            };
        }

        var allSurvivingRows = survivingBuckets.SelectMany(b => b.Rows).ToList();
        if (allSurvivingRows.Count > 0)
        {
            return ComputeBucketFromRows(emptyKey, allSurvivingRows, aggregates, metadata, isMergedOther: false).ToGroupRow();
        }

        return MergePreAggregatedBuckets(emptyKey, survivingBuckets, aggregates, isMergedOther: false).ToGroupRow();
    }

    internal static int ResolveEffectiveMinGroupSize(TableMetadata metadata, AggregationQuery query, int defaultMinGroupSize = 1)
    {
        int resolved = defaultMinGroupSize;
        if (metadata.EffectiveMinGroupSize.HasValue)
        {
            resolved = Math.Max(resolved, metadata.EffectiveMinGroupSize.Value);
        }

        if (query.MinGroupSizeOverride.HasValue)
        {
            resolved = Math.Max(resolved, query.MinGroupSizeOverride.Value);
        }

        return Math.Max(1, resolved);
    }

    private int ResolveEffectiveMinGroupSize(AggregationQuery query, TableMetadata metadata)
    {
        int resolved = _hasExplicitAnalyticsOptions
            ? _analyticsOptions.Privacy.DefaultMinGroupSize
            : 1;

        foreach (var candidateKey in CandidateTableKeys(query.Table))
        {
            if (_analyticsOptions.TableMinimumGroupSizes.TryGetValue(candidateKey, out var tableMin))
            {
                resolved = tableMin;
                break;
            }
        }

        foreach (var candidateKey in CandidateTableKeys(query.Table))
        {
            if (_analyticsOptions.Privacy.Datasets.TryGetValue(candidateKey, out var dsOverride) && dsOverride.MinGroupSize.HasValue)
            {
                resolved = dsOverride.MinGroupSize.Value;
                break;
            }
        }

        // WP-0 (SEC-112-02): every lower-trust source (table metadata, a manifest metric or measure, a request) may only RAISE the
        // floor resolved from the operator configuration; none of them can lower it.
        return ResolveEffectiveMinGroupSize(metadata, query, resolved);
    }

    private SmallGroupStrategy ResolveEffectiveSmallGroupStrategy(AggregationQuery query, TableMetadata metadata)
    {
        var configured = ResolveConfiguredSmallGroupStrategy(query, metadata);

        // WP-0 (SEC-112-02): a request or manifest override may not weaken the configured strategy; suppression is stricter than merging.
        if (query.SmallGroupStrategyOverride.HasValue && configured != SmallGroupStrategy.Suppress)
        {
            return query.SmallGroupStrategyOverride.Value;
        }

        return configured;
    }

    private SmallGroupStrategy ResolveConfiguredSmallGroupStrategy(AggregationQuery query, TableMetadata metadata)
    {
        if (metadata.EffectiveSmallGroupStrategy.HasValue)
        {
            return metadata.EffectiveSmallGroupStrategy.Value;
        }

        foreach (var candidateKey in CandidateTableKeys(query.Table))
        {
            if (_analyticsOptions.Privacy.Datasets.TryGetValue(candidateKey, out var dsOverride) && dsOverride.SuppressionMode.HasValue)
            {
                return dsOverride.SuppressionMode.Value;
            }
        }

        return _analyticsOptions.Privacy.SuppressionMode;
    }

    private static IEnumerable<string> CandidateTableKeys(TableIdentifier table)
    {
        yield return table.ToString();
        yield return table.ToQualifiedName();
        if (!string.IsNullOrWhiteSpace(table.Schema))
        {
            yield return $"{table.Schema}.{table.TableName}";
        }
        yield return table.TableName;
    }

    private static List<ComputedBucket> OrderBuckets(
        List<ComputedBucket> buckets,
        IReadOnlyList<TableOrderBy>? orderBy,
        IReadOnlyList<string> groupByColumns,
        IReadOnlyList<AggregateSelection> aggregates)
    {
        if (buckets.Count <= 1)
        {
            return buckets.ToList();
        }

        IOrderedEnumerable<ComputedBucket>? ordered = null;
        if (orderBy is { Count: > 0 })
        {
            foreach (var item in orderBy)
            {
                Func<ComputedBucket, object?> selector = b =>
                {
                    if (b.GroupKey.TryGetValue(item.Column, out var gk))
                    {
                        return NormalizeComparable(gk);
                    }
                    if (b.Aggregates.TryGetValue(item.Column, out var av))
                    {
                        return NormalizeComparable(av);
                    }
                    if (string.Equals(item.Column, "count", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(item.Column, GovernedTableReadPlanner.GroupCountColumnAlias, StringComparison.OrdinalIgnoreCase))
                    {
                        return b.Count;
                    }
                    return null;
                };

                ordered = ordered == null
                    ? (item.Descending ? buckets.OrderByDescending(selector, ComparableObjectComparer.Instance) : buckets.OrderBy(selector, ComparableObjectComparer.Instance))
                    : (item.Descending ? ordered.ThenByDescending(selector, ComparableObjectComparer.Instance) : ordered.ThenBy(selector, ComparableObjectComparer.Instance));
            }
        }
        else if (groupByColumns.Count > 0)
        {
            foreach (var col in groupByColumns)
            {
                Func<ComputedBucket, object?> selector = b => b.GroupKey.TryGetValue(col, out var gk) ? NormalizeComparable(gk) : null;
                ordered = ordered == null
                    ? buckets.OrderBy(selector, ComparableObjectComparer.Instance)
                    : ordered.ThenBy(selector, ComparableObjectComparer.Instance);
            }
        }

        return ordered?.ToList() ?? buckets.ToList();
    }

    private Task RecordAuditAsync(
        TenantId tenantId,
        Sid userSid,
        TableIdentifier table,
        bool allowed,
        object details,
        CancellationToken ct)
    {
        if (_auditWriter == null)
        {
            return Task.CompletedTask;
        }

        return GovernedQueryStages.RecordTableReadAsync(
            _auditWriter,
            tenantId,
            userSid,
            table,
            allowed,
            details,
            ct,
            AuditTier.Required,
            AuditEventTypes.GovernedAggregation);
    }

    private static object? GetRowValue(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (row.TryGetValue(column, out var exact))
        {
            return exact is DBNull ? null : exact;
        }

        foreach (var (k, v) in row)
        {
            if (string.Equals(k, column, StringComparison.OrdinalIgnoreCase))
            {
                return v is DBNull ? null : v;
            }
        }

        return null;
    }

    private static decimal ToDecimal(object? value) =>
        value switch
        {
            null or DBNull => 0m,
            decimal d => d,
            int i => i,
            long l => l,
            double db => (decimal)db,
            float f => (decimal)f,
            short s => s,
            byte b => b,
            _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture)
        };

    private static object PreferIntegralOrDecimal(decimal value, string? dataType)
    {
        if (!string.IsNullOrWhiteSpace(dataType))
        {
            var dt = dataType.Trim().ToLowerInvariant();
            if ((dt.Contains("int", StringComparison.Ordinal) || dt == "serial") && decimal.Truncate(value) == value)
            {
                return value;
            }
        }

        return value;
    }

    private static object? ComputeMinOrMax(List<object?> values, bool isMax)
    {
        object? best = null;
        foreach (var v in values)
        {
            if (v is null or DBNull)
            {
                continue;
            }

            if (best == null)
            {
                best = v;
                continue;
            }

            int cmp = ComparableObjectComparer.Instance.Compare(NormalizeComparable(v), NormalizeComparable(best));
            if ((isMax && cmp > 0) || (!isMax && cmp < 0))
            {
                best = v;
            }
        }

        return best;
    }

    private static object? NormalizeComparable(object? value) =>
        value switch
        {
            null or DBNull => null,
            int or long or short or byte or decimal or double or float => ToDecimal(value),
            DateTimeOffset dto => dto.UtcTicks,
            DateTime dt => dt.ToUniversalTime().Ticks,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };

    private static bool IsNumericType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return true;
        }

        var dt = dataType.Trim().ToLowerInvariant();
        return dt.Contains("int", StringComparison.Ordinal) ||
               dt.Contains("numeric", StringComparison.Ordinal) ||
               dt.Contains("decimal", StringComparison.Ordinal) ||
               dt.Contains("number", StringComparison.Ordinal) ||
               dt.Contains("float", StringComparison.Ordinal) ||
               dt.Contains("double", StringComparison.Ordinal) ||
               dt.Contains("real", StringComparison.Ordinal) ||
               dt.Contains("money", StringComparison.Ordinal) ||
               dt.Contains("serial", StringComparison.Ordinal) ||
               dt.Contains("hugeint", StringComparison.Ordinal);
    }

    private sealed record ComputedBucket(
        IReadOnlyDictionary<string, object?> GroupKey,
        long Count,
        IReadOnlyDictionary<string, object?> Aggregates,
        IReadOnlyDictionary<string, object?> Sum,
        IReadOnlyDictionary<string, object?> Avg,
        IReadOnlyDictionary<string, object?> Min,
        IReadOnlyDictionary<string, object?> Max,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
        bool IsMergedOther)
    {
        public AggregationGroupRow ToGroupRow() =>
            new(GroupKey, Count, Aggregates, IsMergedOther)
            {
                Sum = Sum,
                Avg = Avg,
                Min = Min,
                Max = Max
            };
    }

    private sealed class ComparableObjectComparer : IComparer<object?>
    {
        public static readonly ComparableObjectComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            if (x is decimal dx && y is decimal dy) return dx.CompareTo(dy);
            if (x is long lx && y is long ly) return lx.CompareTo(ly);
            if (x is IComparable cx && x.GetType() == y.GetType()) return cx.CompareTo(y);
            return string.Compare(
                Convert.ToString(x, CultureInfo.InvariantCulture),
                Convert.ToString(y, CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }
    }

    private static object? TruncateValueToTimeBucket(object? val, TimeBucketSpecification spec)
    {
        if (val is null or DBNull)
        {
            return val;
        }

        DateTimeOffset dto;
        if (val is DateTime dt)
        {
            dto = dt.Kind == DateTimeKind.Unspecified
                ? new DateTimeOffset(dt, TimeSpan.Zero)
                : new DateTimeOffset(dt);
        }
        else if (val is DateTimeOffset o)
        {
            dto = o;
        }
        else if (val is string s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDto))
        {
            dto = parsedDto;
        }
        else
        {
            return val;
        }

        TimeZoneInfo tzi;
        try
        {
            tzi = TimeZoneInfo.FindSystemTimeZoneById(spec.TimeZone);
        }
        catch
        {
            tzi = TimeZoneInfo.Utc;
        }

        var local = TimeZoneInfo.ConvertTime(dto, tzi);
        DateTimeOffset truncated = spec.Interval switch
        {
            TimeBucketInterval.Hour => new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset),
            TimeBucketInterval.Day => new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset),
            TimeBucketInterval.Week => new DateTimeOffset(local.Date.AddDays(-(7 + (local.DayOfWeek - DayOfWeek.Monday)) % 7), local.Offset),
            TimeBucketInterval.Month => new DateTimeOffset(local.Year, local.Month, 1, 0, 0, 0, local.Offset),
            TimeBucketInterval.Quarter => new DateTimeOffset(local.Year, ((local.Month - 1) / 3) * 3 + 1, 1, 0, 0, 0, local.Offset),
            TimeBucketInterval.Year => new DateTimeOffset(local.Year, 1, 1, 0, 0, 0, local.Offset),
            _ => local
        };

        return truncated.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    }
}
