namespace Autheris.Application.Analytics;

using System;
using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// Shared minimum group size post-filter used by both the dbt semantic aggregation path and Databricks metric views (D-MV-8).
/// </summary>
public static class GroupSizeFilterHelper
{
    public static bool MeetsMinGroupSize(long groupCount, int effectiveMinGroupSize) =>
        effectiveMinGroupSize <= 1 || groupCount >= effectiveMinGroupSize;

    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> FilterMetricViewRows(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        int effectiveMinGroupSize,
        string hiddenCountColumnAlias)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(hiddenCountColumnAlias);

        if (effectiveMinGroupSize <= 1)
        {
            return rows;
        }

        var filtered = new List<IReadOnlyDictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            if (!TryReadGroupCount(row, hiddenCountColumnAlias, out var count) || !MeetsMinGroupSize(count, effectiveMinGroupSize))
            {
                continue;
            }

            var cleaned = new Dictionary<string, object?>(row.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in row)
            {
                if (!string.Equals(key, hiddenCountColumnAlias, StringComparison.OrdinalIgnoreCase))
                {
                    cleaned[key] = value;
                }
            }

            filtered.Add(cleaned);
        }

        return filtered;
    }

    private static bool TryReadGroupCount(IReadOnlyDictionary<string, object?> row, string columnName, out long count)
    {
        count = 0;
        if (!row.TryGetValue(columnName, out var raw) || raw is null || raw == DBNull.Value)
        {
            return false;
        }

        try
        {
            count = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }
}
