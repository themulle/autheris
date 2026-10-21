namespace Autheris.Application.Catalog.Profiling;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Issue #80: Generates deterministic, profile-conforming synthetic sample rows without reading real data from the source.
/// Never draws from real values of sensitive or masked columns.
/// </summary>
public sealed class SyntheticSampleRowGenerator(IColumnValueProfileService? profileService = null) : ISyntheticSampleRowGenerator
{
    private readonly IColumnValueProfileService _profileService = profileService ?? new ColumnValueProfileService();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> _registeredSamples = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterSyntheticSamples(TableIdentifier table, IReadOnlyList<IReadOnlyDictionary<string, object?>> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        _registeredSamples[table.ToString()] = samples;
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>>? GetRegisteredSyntheticSamples(TableIdentifier table)
    {
        return _registeredSamples.TryGetValue(table.ToString(), out var samples) ? samples : null;
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> GenerateRows(
        TableMetadata table,
        int count,
        IReadOnlyDictionary<string, ColumnAccessLevel>? columnAccessLevels = null,
        bool hasActiveRowFilters = false,
        ColumnProfileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        int rowCount = Math.Clamp(count, 1, 100);

        var rows = new List<IReadOnlyDictionary<string, object?>>(rowCount);

        var predefined = (table.PredefinedSyntheticSamples is { Count: > 0 } p && p.Count > 0)
            ? p
            : GetRegisteredSyntheticSamples(table.Identifier);

        if (predefined is { Count: > 0 })
        {
            foreach (var preRow in predefined)
            {
                if (rows.Count >= rowCount) break;

                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["__synthetic"] = true
                };

                foreach (var column in table.Columns)
                {
                    var level = ColumnAccessLevel.Clear;
                    if (columnAccessLevels != null && columnAccessLevels.TryGetValue(column.ColumnName, out var explicitLevel))
                    {
                        level = explicitLevel;
                    }

                    if (level == ColumnAccessLevel.Deny)
                    {
                        continue;
                    }

                    // Requirement: Sample data should not be masked.
                    // Kept in plain text for non-denied columns.
                    object? val = null;
                    if (preRow.TryGetValue(column.ColumnName, out var exactVal))
                    {
                        val = exactVal;
                    }
                    else
                    {
                        foreach (var kvp in preRow)
                        {
                            if (string.Equals(kvp.Key, column.ColumnName, StringComparison.OrdinalIgnoreCase))
                            {
                                val = kvp.Value;
                                break;
                            }
                        }
                    }

                    row[column.ColumnName] = val;
                }

                rows.Add(row);
            }

            if (rows.Count >= rowCount)
            {
                return rows;
            }
        }

        var primaryKeys = new HashSet<string>(table.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);
        if (primaryKeys.Count == 0 && table.HasColumn("id"))
        {
            primaryKeys.Add("id");
        }

        var profiles = _profileService.GetDatasetProfiles(table, columnAccessLevels, hasActiveRowFilters, options);

        for (int rowIndex = rows.Count; rowIndex < rowCount; rowIndex++)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["__synthetic"] = true
            };

            foreach (var column in table.Columns)
            {
                var level = ColumnAccessLevel.Clear;
                if (columnAccessLevels != null && columnAccessLevels.TryGetValue(column.ColumnName, out var explicitLevel))
                {
                    level = explicitLevel;
                }
                else if (column.IsSensitive || (table.ColumnMaskingRules != null && table.ColumnMaskingRules.ContainsKey(column.ColumnName)))
                {
                    level = ColumnAccessLevel.Mask;
                }

                if (level == ColumnAccessLevel.Deny)
                {
                    continue;
                }

                bool isPrimaryKey = primaryKeys.Contains(column.ColumnName);
                bool isSensitiveOrMasked = ColumnValueProfileService.IsColumnSensitiveOrMasked(table, column, level);
                profiles.TryGetValue(column.ColumnName, out var profile);

                row[column.ColumnName] = GenerateColumnValue(
                    column,
                    profile,
                    rowIndex,
                    rowCount,
                    isPrimaryKey,
                    isSensitiveOrMasked);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static object? GenerateColumnValue(
        TableColumn column,
        ColumnValueProfile? profile,
        int rowIndex,
        int totalRows,
        bool isPrimaryKey,
        bool isSensitiveOrMasked)
    {
        double emptyShare = profile?.EmptyShare ?? 0.0;
        if (!isPrimaryKey && emptyShare > 0.0)
        {
            if (emptyShare >= 1.0)
            {
                return null;
            }

            int expectedNullCount = (int)Math.Round(totalRows * emptyShare, MidpointRounding.AwayFromZero);
            // Place nulls at the tail of the batch deterministically so non-null rows remain easy to inspect first.
            if (expectedNullCount > 0 && rowIndex >= totalRows - expectedNullCount)
            {
                return null;
            }
        }

        // Security Rule: Sensitive or masked columns NEVER draw from real values or snapshots.
        if (isSensitiveOrMasked)
        {
            return GenerateSafeSensitivePlaceholder(column, rowIndex);
        }

        if (isPrimaryKey)
        {
            return GeneratePrimaryKeyValue(column, profile, rowIndex);
        }

        if (profile?.AllowedValues is { Count: > 0 } allowed)
        {
            return allowed[rowIndex % allowed.Count];
        }

        if (!string.IsNullOrWhiteSpace(profile?.FormatPattern))
        {
            return GenerateFromFormatPattern(profile.FormatPattern!, profile, column.DataType, rowIndex);
        }

        return GenerateByDataTypeAndRange(column, profile, rowIndex, totalRows);
    }

    private static object GenerateSafeSensitivePlaceholder(TableColumn column, int rowIndex)
    {
        var type = (column.DataType ?? "varchar").ToLowerInvariant();
        int seq = rowIndex + 1;

        if (type.Contains("int") || type is "long" or "bigint" or "smallint")
        {
            return 9000 + seq;
        }

        if (type.Contains("dec") || type.Contains("num") || type.Contains("float") || type.Contains("double") || type.Contains("money"))
        {
            return 0.00m;
        }

        if (type.Contains("bool") || type == "bit")
        {
            return false;
        }

        if (type.Contains("date") || type.Contains("time"))
        {
            return "1970-01-01";
        }

        if (type.Contains("uuid") || type.Contains("uniqueidentifier"))
        {
            return $"00000000-0000-0000-0000-{seq:D12}";
        }

        var colNameLower = column.ColumnName.ToLowerInvariant();
        if (colNameLower.Contains("email"))
        {
            return $"synthetic.redacted.{seq}@example.invalid";
        }

        return $"SYNTHETIC_REDACTED_{seq:D4}";
    }

    private static object GeneratePrimaryKeyValue(TableColumn column, ColumnValueProfile? profile, int rowIndex)
    {
        int seq = rowIndex + 1;
        var type = (column.DataType ?? "int").ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(profile?.FormatPattern) &&
            !string.Equals(profile.FormatPattern, "ISO-8601-Date", StringComparison.OrdinalIgnoreCase))
        {
            return ExpandPatternMask(profile.FormatPattern!, seq);
        }

        if (type.Contains("uuid") || type.Contains("uniqueidentifier"))
        {
            return $"10000000-0000-4000-8000-{seq:D12}";
        }

        if (type.Contains("char") || type.Contains("text") || type.Contains("string"))
        {
            return $"PK-{seq:D6}";
        }

        if (long.TryParse(profile?.MinValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minPk) && minPk > 0)
        {
            return minPk + rowIndex;
        }

        return seq;
    }

    private static object GenerateFromFormatPattern(string pattern, ColumnValueProfile profile, string? dataType, int rowIndex)
    {
        int seq = rowIndex + 1;
        if (string.Equals(pattern, "ISO-8601-Date", StringComparison.OrdinalIgnoreCase))
        {
            return InterpolateDate(profile.MinValue, profile.MaxValue, rowIndex, includeTime: false);
        }

        if (string.Equals(pattern, "ISO-8601-DateTime", StringComparison.OrdinalIgnoreCase))
        {
            return InterpolateDate(profile.MinValue, profile.MaxValue, rowIndex, includeTime: true);
        }

        if (string.Equals(pattern, "email", StringComparison.OrdinalIgnoreCase))
        {
            return $"synthetic.user{seq}@example.test";
        }

        if (string.Equals(pattern, "uuid", StringComparison.OrdinalIgnoreCase))
        {
            return $"20000000-0000-4000-8000-{seq:D12}";
        }

        return ExpandPatternMask(pattern, seq);
    }

    private static string ExpandPatternMask(string mask, int sequenceNumber)
    {
        var sb = new StringBuilder(mask.Length);
        int digitSlots = mask.Count(c => c is '0' or '#');
        string seqDigits = sequenceNumber.ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(1, digitSlots), '0');
        int digitIndex = Math.Max(0, seqDigits.Length - digitSlots);
        int letterOffset = 0;

        foreach (char c in mask)
        {
            if (c is '0' or '#')
            {
                sb.Append(digitIndex < seqDigits.Length ? seqDigits[digitIndex++] : '0');
            }
            else if (c == 'A')
            {
                char letter = (char)('A' + ((sequenceNumber - 1 + letterOffset++) % 26));
                sb.Append(letter);
            }
            else if (c == 'a')
            {
                char letter = (char)('a' + ((sequenceNumber - 1 + letterOffset++) % 26));
                sb.Append(letter);
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static object GenerateByDataTypeAndRange(TableColumn column, ColumnValueProfile? profile, int rowIndex, int totalRows)
    {
        var type = (column.DataType ?? "varchar").ToLowerInvariant();
        int seq = rowIndex + 1;

        if (type.Contains("int") || type is "long" or "bigint" or "smallint" or "tinyint")
        {
            long min = 10;
            long max = 100;
            if (long.TryParse(profile?.MinValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedMin))
            {
                min = parsedMin;
                max = parsedMin + 100;
            }
            if (long.TryParse(profile?.MaxValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedMax) && parsedMax >= min)
            {
                max = parsedMax;
            }

            if (totalRows <= 1 || max == min)
            {
                return min;
            }

            long step = Math.Max(1L, (max - min) / Math.Max(1, totalRows - 1));
            long candidate = min + (rowIndex * step);
            return Math.Clamp(candidate, min, max);
        }

        if (type.Contains("dec") || type.Contains("num") || type.Contains("float") || type.Contains("double") || type.Contains("money"))
        {
            decimal min = 10.00m;
            decimal max = 500.00m;
            if (decimal.TryParse(profile?.MinValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedMin))
            {
                min = parsedMin;
                max = parsedMin + 100m;
            }
            if (decimal.TryParse(profile?.MaxValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedMax) && parsedMax >= min)
            {
                max = parsedMax;
            }

            if (totalRows <= 1 || max == min)
            {
                return Math.Round(min, 2);
            }

            decimal ratio = (decimal)rowIndex / Math.Max(1, totalRows - 1);
            return Math.Round(min + ((max - min) * ratio), 2);
        }

        if (type.Contains("bool") || type == "bit")
        {
            return rowIndex % 2 == 0;
        }

        if (type.Contains("date") || type.Contains("time"))
        {
            return InterpolateDate(profile?.MinValue, profile?.MaxValue, rowIndex, includeTime: type.Contains("time") && !string.Equals(type, "date", StringComparison.OrdinalIgnoreCase));
        }

        if (type.Contains("uuid") || type.Contains("uniqueidentifier"))
        {
            return $"30000000-0000-4000-8000-{seq:D12}";
        }

        string baseText = $"synthetic_{column.ColumnName}_{seq}";
        if (profile?.TypicalLength is > 0 and <= 128)
        {
            int targetLen = profile.TypicalLength.Value;
            if (baseText.Length > targetLen)
            {
                return baseText[..targetLen];
            }
            if (baseText.Length < targetLen)
            {
                return baseText.PadRight(targetLen, 'x');
            }
        }

        return baseText;
    }

    private static string InterpolateDate(string? minStr, string? maxStr, int rowIndex, bool includeTime)
    {
        var baseDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var maxDate = baseDate.AddDays(30);

        if (DateTimeOffset.TryParse(minStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedMin))
        {
            baseDate = parsedMin;
            maxDate = baseDate.AddDays(30);
        }

        if (DateTimeOffset.TryParse(maxStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedMax) && parsedMax >= baseDate)
        {
            maxDate = parsedMax;
        }

        double totalDays = Math.Max(0, (maxDate - baseDate).TotalDays);
        double offsetDays = totalDays > 0 ? Math.Min(totalDays, rowIndex) : 0;
        var current = baseDate.AddDays(offsetDays);

        return includeTime
            ? current.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            : current.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
