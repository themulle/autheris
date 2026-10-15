namespace Autheris.Application.Catalog.Profiling;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

public sealed partial class ColumnValueProfileService
{
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
