namespace Autheris.Application.Catalog.Profiling;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Issue #80: Computes and serves coarsened <see cref="ColumnValueProfile"/> metadata from declared dbt/catalog metadata
/// and scheduled snapshots without touching the data source at <c>describe_dataset</c> time.
/// </summary>
public sealed class ColumnValueProfileService : IColumnValueProfileService
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
        var numericValues = new List< decimal>();
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
                if ( decimal.TryParse(Convert.ToString(val, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
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
        else if (nonEmptyStrings.Count > 0)
        {
            minValue = nonEmptyStrings.Min(StringComparer.Ordinal);
            maxValue = nonEmptyStrings.Max(StringComparer.Ordinal);
        }

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
        else if (distinctCount is > 20 && snapshot?.ObservedAllowedValues is { Count: > 0 } obsSamples)
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

    private static string? ComputeDistinctCountBucket(long? distinctCount)
    {
        if (!distinctCount.HasValue || distinctCount.Value <= 0)
        {
            return null;
        }

        long c = distinctCount.Value;
        if (c < 10) return "<10";
        if (c < 100) return "10-100";
        if (c < 1_000) return "100-1k";
        if (c < 10_000) return "1k-10k";
        return ">10k";
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

    public static double CoarsenEmptyShare(double rawEmptyShare)
    {
        if (double.IsNaN(rawEmptyShare) || double.IsInfinity(rawEmptyShare))
        {
            return 0.0;
        }

        var clamped = Math.Clamp(rawEmptyShare, 0.0, 1.0);
        return Math.Round(clamped * 20.0, MidpointRounding.AwayFromZero) / 20.0;
    }

    public static bool IsColumnSensitiveOrMasked(
        TableMetadata table,
        TableColumn column,
        ColumnAccessLevel effectiveAccessLevel)
    {
        if (effectiveAccessLevel != ColumnAccessLevel.Clear)
        {
            return true;
        }

        if (column.IsSensitive)
        {
            return true;
        }

        if (table.ColumnMaskingRules != null && table.ColumnMaskingRules.ContainsKey(column.ColumnName))
        {
            return true;
        }

        if (column.Meta != null)
        {
            if ((column.Meta.TryGetValue("is_pii", out var pii) && string.Equals(pii, "true", StringComparison.OrdinalIgnoreCase)) ||
                (column.Meta.TryGetValue("pii", out var pii2) && string.Equals(pii2, "true", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (column.Meta.TryGetValue("sensitivity", out var sens) && !string.IsNullOrWhiteSpace(sens))
            {
                var normalized = sens.Trim().ToUpperInvariant();
                if (normalized is not ("PUBLIC" or "INTERNAL" or "LOW" or "NORMAL"))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsValueListReleased(TableColumn column, ColumnProfileOptions? options = null)
    {
        if (options?.ReleaseAllowedValues == true)
        {
            return true;
        }

        if (column.Meta != null)
        {
            if (column.Meta.TryGetValue("release_value_list", out var rvl) &&
                string.Equals(rvl?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (column.Meta.TryGetValue("release_allowed_values", out var rav) &&
                string.Equals(rav?.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static long DetermineEffectiveRowCount(TableMetadata table, TableColumn column, ColumnProfileSnapshot? snapshot)
    {
        if (table.EstimatedRowCount.HasValue)
        {
            return table.EstimatedRowCount.Value;
        }

        if (column.Meta != null &&
            column.Meta.TryGetValue("table_row_count", out var trc) &&
            long.TryParse(trc, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        if (snapshot != null)
        {
            return snapshot.TableRowCount;
        }

        return 100L;
    }

    private static (string? Min, string? Max) CoarsenRangeForSmallTable(string? min, string? max)
    {
        if (min == null && max == null)
        {
            return (null, null);
        }

        if (decimal.TryParse(min, NumberStyles.Number, CultureInfo.InvariantCulture, out var minDec) &&
            decimal.TryParse(max, NumberStyles.Number, CultureInfo.InvariantCulture, out var maxDec))
        {
            // Bucket to 10-steps (or order-of-magnitude steps for larger numbers) so a single record is never revealed.
            decimal step = Math.Max(10m, (decimal)Math.Pow(10, Math.Max(1, (int)Math.Floor(Math.Log10((double)Math.Max(1m, Math.Abs(maxDec)))))));
            decimal bucketMin = Math.Floor(minDec / step) * step;
            decimal bucketMax = Math.Ceiling(maxDec / step) * step;
            if (bucketMax <= bucketMin)
            {
                bucketMax = bucketMin + step;
            }

            return (bucketMin.ToString("0.##", CultureInfo.InvariantCulture), bucketMax.ToString("0.##", CultureInfo.InvariantCulture));
        }

        if (DateTimeOffset.TryParse(min, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var minDate) &&
            DateTimeOffset.TryParse(max, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var maxDate))
        {
            // Coarsen dates on small tables to whole-year boundaries.
            return ($"{minDate.Year:D4}-01-01", $"{maxDate.Year:D4}-12-31");
        }

        // Free-text min/max on small tables could identify a single row; suppress exact string endpoints.
        return (null, null);
    }

    private static long? CoarsenDistinctCountForSmallTable(long? count)
    {
        if (!count.HasValue)
        {
            return null;
        }

        if (count.Value <= 0)
        {
            return 0L;
        }

        // Round to 10-bucket (minimum 10) so exact small cardinality is not revealed.
        return Math.Max(10L, ((count.Value + 9) / 10) * 10L);
    }

    private static (string? Min, string? Max) ExtractDeclaredRange(IReadOnlyDictionary<string, string> meta)
    {
        var min = GetMetaString(meta, "min_value") ?? GetMetaString(meta, "min");
        var max = GetMetaString(meta, "max_value") ?? GetMetaString(meta, "max");

        if (meta.TryGetValue("accepted_range", out var rangeRaw) && !string.IsNullOrWhiteSpace(rangeRaw))
        {
            var trimmed = rangeRaw.Trim();
            if (trimmed.StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    if (doc.RootElement.TryGetProperty("min", out var minProp))
                    {
                        min ??= minProp.ToString();
                    }
                    if (doc.RootElement.TryGetProperty("max", out var maxProp))
                    {
                        max ??= maxProp.ToString();
                    }
                }
                catch (JsonException)
                {
                }
            }
            else if (trimmed.Contains("..", StringComparison.Ordinal))
            {
                var parts = trimmed.Trim('[', ']', '(', ')').Split("..", 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                {
                    if (!string.IsNullOrWhiteSpace(parts[0])) min ??= parts[0];
                    if (!string.IsNullOrWhiteSpace(parts[1])) max ??= parts[1];
                }
            }
            else if (trimmed.Contains(',', StringComparison.Ordinal))
            {
                var parts = trimmed.Trim('[', ']', '(', ')').Split(',', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                {
                    if (!string.IsNullOrWhiteSpace(parts[0])) min ??= parts[0];
                    if (!string.IsNullOrWhiteSpace(parts[1])) max ??= parts[1];
                }
            }
        }

        return (min, max);
    }

    internal static IReadOnlyList<string>? ExtractDeclaredAllowedValues(IReadOnlyDictionary<string, string> meta)
    {
        if (!meta.TryGetValue("accepted_values", out var raw) && !meta.TryGetValue("allowed_values", out raw))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(trimmed);
                if (parsed is { Count: > 0 })
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
            }
        }

        var items = trimmed
            .Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Trim('\'', '"'))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        return items.Count > 0 ? items : null;
    }

    private static string? GetMetaString(IReadOnlyDictionary<string, string> meta, string key) =>
        meta.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val) ? val.Trim() : null;

    private static string ConvertToString(object val) =>
        val switch
        {
            DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => val.ToString() ?? string.Empty
        };

    private static string? InferFormatPattern(IReadOnlyList<string> samples, string? dataType)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        if (samples.All(s => Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}$")))
        {
            return "ISO-8601-Date";
        }

        if (samples.All(s => Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}")))
        {
            return "ISO-8601-DateTime";
        }

        if (samples.All(s => s.Contains('@') && s.Contains('.')))
        {
            return "email";
        }

        if (samples.All(s => Guid.TryParse(s, out _)))
        {
            return "uuid";
        }

        // Check if all samples share a fixed character-class mask like "AA-000000"
        string firstMask = BuildCharacterMask(samples[0]);
        if (firstMask.Length is >= 3 and <= 32 &&
            firstMask.Any(c => c is 'A' or '0') &&
            samples.All(s => string.Equals(BuildCharacterMask(s), firstMask, StringComparison.Ordinal)))
        {
            return firstMask;
        }

        return dataType?.ToLowerInvariant() switch
        {
            "date" => "ISO-8601-Date",
            "datetime" or "datetime2" or "timestamp" or "timestamptz" => "ISO-8601-DateTime",
            "uuid" or "uniqueidentifier" => "uuid",
            _ => null
        };
    }

    private static string BuildCharacterMask(string input)
    {
        Span<char> chars = stackalloc char[Math.Min(input.Length, 64)];
        for (int i = 0; i < chars.Length; i++)
        {
            char c = input[i];
            if (char.IsAsciiLetterUpper(c)) chars[i] = 'A';
            else if (char.IsAsciiLetterLower(c)) chars[i] = 'a';
            else if (char.IsAsciiDigit(c)) chars[i] = '0';
            else chars[i] = c;
        }

        return new string(chars);
    }
}
