namespace Autheris.Application.Sql.Federation.KeySet;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// WP-124-2: Key batching, distinct key streaming, and bounds clamping.
/// Enforces SEC-DATA124-03: MaxKeySetKeys <= 2000, MaxKeySetBatches <= 10, SQL Server parameter ceiling <= 2090.
/// </summary>
public static class KeyBatcher
{
    public const int SqlServerParameterLimit = 2090;
    public const int SqliteParameterLimit = 999;
    public const int OracleParameterLimit = 1000;
    public const int DefaultMaxKeys = 2000;
    public const int DefaultMaxBatches = 10;
    public const int DefaultBatchSize = 500;

    /// <summary>
    /// Streams distinct, non-null join key values from staged row dictionaries.
    /// Preserves ordinal string comparison semantics and skips nulls/DBNull.
    /// </summary>
    public static IReadOnlyList<object> ExtractDistinctKeys(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        string keyColumn)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyColumn);

        if (rows.Count == 0)
        {
            return Array.Empty<object>();
        }

        var distinctKeys = new List<object>();
        var seenKeys = new HashSet<object>(new KeyEqualityComparer());

        foreach (var row in rows)
        {
            if (!TryGetColumnValue(row, keyColumn, out var val) || val is null or DBNull)
            {
                continue;
            }

            if (seenKeys.Add(val))
            {
                distinctKeys.Add(val);
            }
        }

        return distinctKeys;
    }

    /// <summary>
    /// Resolves the effective batch size clamped against dialect parameter ceilings (SEC-DATA124-03).
    /// </summary>
    public static int ResolveBatchSize(DatabaseDialect dialect, int configuredBatchSize, int columnsPerKey = 1)
    {
        int dialectCeiling = dialect switch
        {
            DatabaseDialect.SqlServer => SqlServerParameterLimit,
            DatabaseDialect.Sqlite => SqliteParameterLimit,
            DatabaseDialect.Oracle => OracleParameterLimit,
            _ => SqlServerParameterLimit // Safe default ceiling across all dialects
        };

        int cols = Math.Max(1, columnsPerKey);
        int maxKeysForDialect = Math.Max(1, dialectCeiling / cols);
        int requested = configuredBatchSize > 0 ? configuredBatchSize : DefaultBatchSize;

        return Math.Max(1, Math.Min(requested, maxKeysForDialect));
    }

    /// <summary>
    /// SEC-DATA124-03: Checks whether extracted key count and resulting batch count stay within bounds.
    /// </summary>
    public static bool IsWithinBounds(
        int keyCount,
        int batchCount,
        int maxKeys = DefaultMaxKeys,
        int maxBatches = DefaultMaxBatches)
    {
        return keyCount <= maxKeys && batchCount <= maxBatches;
    }

    /// <summary>
    /// Partitions keys into batches of at most <paramref name="batchSize"/>.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<object>> CreateBatches(
        IReadOnlyList<object> keys,
        int batchSize)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be greater than zero.");

        if (keys.Count == 0)
        {
            return Array.Empty<IReadOnlyList<object>>();
        }

        var batches = new List<IReadOnlyList<object>>((keys.Count + batchSize - 1) / batchSize);
        for (int offset = 0; offset < keys.Count; offset += batchSize)
        {
            int count = Math.Min(batchSize, keys.Count - offset);
            var batch = new object[count];
            for (int i = 0; i < count; i++)
            {
                batch[i] = keys[offset + i];
            }
            batches.Add(batch);
        }

        return batches;
    }

    private static bool TryGetColumnValue(IReadOnlyDictionary<string, object?> row, string columnName, out object? value)
    {
        if (row.TryGetValue(columnName, out value))
        {
            return true;
        }

        foreach (var (k, v) in row)
        {
            if (string.Equals(k, columnName, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }

        value = null;
        return false;
    }

    private sealed class KeyEqualityComparer : IEqualityComparer<object>
    {
        public new bool Equals(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;

            if (x is string sx && y is string sy)
            {
                return string.Equals(sx, sy, StringComparison.Ordinal);
            }

            return x.Equals(y);
        }

        public int GetHashCode(object obj)
        {
            if (obj is string s)
            {
                return StringComparer.Ordinal.GetHashCode(s);
            }

            return obj.GetHashCode();
        }
    }
}
