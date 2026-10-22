namespace Autheris.Application.Catalog.Profiling;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Issue #80: Computes and serves coarsened <see cref="ColumnValueProfile"/> metadata from declared dbt/catalog metadata
/// and scheduled snapshots without touching the data source at <c>describe_dataset</c> time.
/// </summary>
public sealed partial class ColumnValueProfileService : IColumnValueProfileService
{
    private readonly ConcurrentDictionary<string, ColumnProfileSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    private static string BuildKey(TableIdentifier table, string columnName) =>
        $"{table.Domain}.{table.Schema}.{table.TableName}:{columnName}".ToLowerInvariant();

    public void StoreProfileSnapshot(ColumnProfileSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.ColumnName);
        _snapshots[BuildKey(snapshot.Table, snapshot.ColumnName)] = snapshot;
    }

    public ColumnProfileSnapshot? GetStoredSnapshot(TableIdentifier table, string columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName))
        {
            return null;
        }

        _snapshots.TryGetValue(BuildKey(table, columnName), out var snapshot);
        return snapshot;
    }

    public Task<ColumnProfileSnapshot> ComputeAndStoreProfileAsync(
        TableMetadata table,
        string columnName,
        IEnumerable<object?> values,
        ColumnProfileOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        ArgumentNullException.ThrowIfNull(values);
        ct.ThrowIfCancellationRequested();

        var materialized = values as IList<object?> ?? values.ToList();
        var totalCount = materialized.Count;
        if (totalCount == 0)
        {
            var emptySnapshot = new ColumnProfileSnapshot(
                table.Identifier,
                columnName,
                EmptyShare: 1.0,
                TableRowCount: table.EstimatedRowCount ?? 0,
                ComputedAt: DateTimeOffset.UtcNow);
            StoreProfileSnapshot(emptySnapshot);
            return Task.FromResult(emptySnapshot);
        }

        int emptyCount = 0;
        var nonEmptyStrings = new List<string>(totalCount);
        var numericValues = new List<decimal>();
        var dateValues = new List<DateTimeOffset>();

        foreach (var val in materialized)
        {
            if (val is null or DBNull)
            {
                emptyCount++;
                continue;
            }

            var str = ConvertToString(val);
            if (string.IsNullOrWhiteSpace(str))
            {
                emptyCount++;
                continue;
            }

            nonEmptyStrings.Add(str);
            if (val is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal)
            {
                if (decimal.TryParse(Convert.ToString(val, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                {
                    numericValues.Add(d);
                }
            }
            else if (val is DateTime dt)
            {
                dateValues.Add(new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt));
            }
            else if (val is DateTimeOffset dto)
            {
                dateValues.Add(dto);
            }
            else if (val is DateOnly dateOnly)
            {
                dateValues.Add(new DateTimeOffset(dateOnly.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
            }
            else if (decimal.TryParse(str, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedDec))
            {
                numericValues.Add(parsedDec);
            }
            else if (DateTimeOffset.TryParse(str, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate) &&
                     str.Length >= 8 && str.Contains('-'))
            {
                dateValues.Add(parsedDate);
            }
        }

        double rawEmptyShare = (double)emptyCount / totalCount;
        double coarsenedEmptyShare = CoarsenEmptyShare(rawEmptyShare);

        string? minValue = null;
        string? maxValue = null;
        if (numericValues.Count == nonEmptyStrings.Count && numericValues.Count > 0)
        {
            minValue = numericValues.Min().ToString(CultureInfo.InvariantCulture);
            maxValue = numericValues.Max().ToString(CultureInfo.InvariantCulture);
        }
        else if (dateValues.Count == nonEmptyStrings.Count && dateValues.Count > 0)
        {
            minValue = dateValues.Min().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            maxValue = dateValues.Max().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        // MCP-16: lexicographic string extremes are real values and are never published by default.

        int? typicalLength = nonEmptyStrings.Count > 0
            ? (int)Math.Round(nonEmptyStrings.Average(s => s.Length), MidpointRounding.AwayFromZero)
            : null;

        var distinctValues = nonEmptyStrings.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        long? distinctCount = nonEmptyStrings.Count > 0 ? distinctValues.Count : 0L;

        var colMeta = table.GetColumn(columnName);
        string? formatPattern = InferFormatPattern(nonEmptyStrings, colMeta?.DataType);

        IReadOnlyList<string>? observedAllowed = distinctValues.Count switch
        {
            0 => null,
            <= 20 => distinctValues,
            _ => distinctValues.Take(5).ToList()
        };
        long effectiveTableRows = table.EstimatedRowCount ?? totalCount;

        var snapshot = new ColumnProfileSnapshot(
            Table: table.Identifier,
            ColumnName: columnName,
            FormatPattern: formatPattern,
            MinValue: minValue,
            MaxValue: maxValue,
            EmptyShare: coarsenedEmptyShare,
            TypicalLength: typicalLength,
            ApproximateDistinctCount: distinctCount,
            ObservedAllowedValues: observedAllowed,
            TableRowCount: effectiveTableRows,
            ComputedAt: DateTimeOffset.UtcNow);

        StoreProfileSnapshot(snapshot);
        return Task.FromResult(snapshot);
    }

    public async Task<IReadOnlyList<ColumnProfileSnapshot>> ComputeAndStoreDatasetProfilesAsync(
        TableMetadata table,
        IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        ColumnProfileOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(rows);

        var rowList = rows as IList<IReadOnlyDictionary<string, object?>> ?? rows.ToList();
        var tableWithCount = table.EstimatedRowCount.HasValue
            ? table
            : table with { EstimatedRowCount = rowList.Count };

        var snapshots = new List<ColumnProfileSnapshot>(table.Columns.Count);
        foreach (var column in table.Columns)
        {
            var colValues = rowList
                .Select(r => r.TryGetValue(column.ColumnName, out var v) ? v : null)
                .ToList();
            var snap = await ComputeAndStoreProfileAsync(tableWithCount, column.ColumnName, colValues, options, ct).ConfigureAwait(false);
            snapshots.Add(snap);
        }

        return snapshots;
    }

    public ColumnValueProfile? GetColumnProfile(
        TableMetadata table,
        TableColumn column,
        ColumnAccessLevel effectiveAccessLevel = ColumnAccessLevel.Clear,
        bool hasActiveRowFilters = false,
        ColumnProfileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(column);

        if (effectiveAccessLevel == ColumnAccessLevel.Deny)
        {
            return null;
        }

        var snapshot = GetStoredSnapshot(table.Identifier, column.ColumnName);
        var meta = column.Meta ?? new Dictionary<string, string>();

        double rawEmptyShare = snapshot?.EmptyShare ?? 0.0;
        if (meta.TryGetValue("empty_share", out var esStr) &&
            double.TryParse(esStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedEs))
        {
            rawEmptyShare = parsedEs;
        }

        double coarsenedEmptyShare = CoarsenEmptyShare(rawEmptyShare);

        string? formatPattern = GetMetaString(meta, "format")
            ?? GetMetaString(meta, "pattern")
            ?? GetMetaString(meta, "format_pattern")
            ?? snapshot?.FormatPattern;
        if (formatPattern == null && column.ColumnName.Contains("email", StringComparison.OrdinalIgnoreCase))
        {
            formatPattern = "email";
        }

        // Privacy Rule 1: Sensitive or masked columns never expose AllowedValues, SampleValues, Min, or Max.
        if (IsColumnSensitiveOrMasked(table, column, effectiveAccessLevel))
        {
            return new ColumnValueProfile(
                FormatPattern: formatPattern,
                MinValue: null,
                MaxValue: null,
                EmptyShare: coarsenedEmptyShare,
                TypicalLength: snapshot?.TypicalLength,
                ApproximateDistinctCount: null,
                AllowedValues: null,
                DistinctCountBucket: null,
                SampleValues: null);
        }

        var (declaredMin, declaredMax) = ExtractDeclaredRange(meta);
        string? minValue = declaredMin ?? snapshot?.MinValue;
        string? maxValue = declaredMax ?? snapshot?.MaxValue;

        int? typicalLength = snapshot?.TypicalLength;
        if (meta.TryGetValue("typical_length", out var tlStr) &&
            int.TryParse(tlStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTl))
        {
            typicalLength = parsedTl;
        }

        long? distinctCount = snapshot?.ApproximateDistinctCount;
        if (meta.TryGetValue("distinct_count", out var dcStr) &&
            long.TryParse(dcStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDc))
        {
            distinctCount = parsedDc;
        }

        // Privacy Rule 2: Low-cardinality AllowedValues vs high-cardinality SampleValues + DistinctCountBucket.
        var declaredAllowed = ExtractDeclaredAllowedValues(meta);
        IReadOnlyList<string>? allowedValues = null;
        IReadOnlyList<string>? sampleValues = null;
        if (declaredAllowed is { Count: > 0 } &&
            (!meta.TryGetValue("release_value_list", out var rvlFlag) || !string.Equals(rvlFlag, "false", StringComparison.OrdinalIgnoreCase)))
        {
            allowedValues = declaredAllowed;
        }
        else if (IsValueListReleased(column, options) && snapshot?.ObservedAllowedValues is { Count: > 0 and <= 20 } obsLow)
        {
            allowedValues = obsLow;
        }
        // MCP-16: real observed samples need the same release decision as the value list.
        else if (IsValueListReleased(column, options) && distinctCount is > 20 && snapshot?.ObservedAllowedValues is { Count: > 0 } obsSamples)
        {
            sampleValues = obsSamples.Take(5).ToList();
        }

        string? distinctBucket = ComputeDistinctCountBucket(distinctCount);

        // Privacy Rule 3: Small-table suppression (< 100 rows by default or < MinRowsForExactRange).
        int minRows = options?.MinRowsForExactRange ?? 100;
        long effectiveRowCount = DetermineEffectiveRowCount(table, column, snapshot);
        if (effectiveRowCount < minRows)
        {
            minValue = null;
            maxValue = null;
            sampleValues = null;
            distinctCount = CoarsenDistinctCountForSmallTable(distinctCount);
        }

        // Privacy Rule 4: Row-filter publication policy on tables with active row filters.
        if (hasActiveRowFilters && table.ProfilePublicationPolicy == DatasetProfilePublicationPolicy.FormatAndEmptyShareOnly)
        {
            minValue = null;
            maxValue = null;
            distinctCount = null;
            distinctBucket = null;
            allowedValues = null;
            sampleValues = null;
        }

        return new ColumnValueProfile(
            FormatPattern: formatPattern,
            MinValue: minValue,
            MaxValue: maxValue,
            EmptyShare: coarsenedEmptyShare,
            TypicalLength: typicalLength,
            ApproximateDistinctCount: distinctCount,
            AllowedValues: allowedValues,
            DistinctCountBucket: distinctBucket,
            SampleValues: sampleValues);
    }

    public IReadOnlyDictionary<string, ColumnValueProfile> GetDatasetProfiles(
        TableMetadata table,
        IReadOnlyDictionary<string, ColumnAccessLevel>? columnAccessLevels = null,
        bool hasActiveRowFilters = false,
        ColumnProfileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        var result = new Dictionary<string, ColumnValueProfile>(StringComparer.OrdinalIgnoreCase);

        foreach (var col in table.Columns)
        {
            var level = ColumnAccessLevel.Clear;
            if (columnAccessLevels != null && columnAccessLevels.TryGetValue(col.ColumnName, out var explicitLevel))
            {
                level = explicitLevel;
            }
            else if (col.IsSensitive || (table.ColumnMaskingRules != null && table.ColumnMaskingRules.ContainsKey(col.ColumnName)))
            {
                level = ColumnAccessLevel.Mask;
            }

            var profile = GetColumnProfile(table, col, level, hasActiveRowFilters, options);
            if (profile != null)
            {
                result[col.ColumnName] = profile;
            }
        }

        return result;
    }
}
