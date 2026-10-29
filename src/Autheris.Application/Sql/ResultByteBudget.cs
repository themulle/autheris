namespace Autheris.Application.Sql;

using System;
using System.Buffers;
using System.Data.Common;
using System.Text;
using Autheris.Application.Analytics;

/// <summary>
/// #398 (I398-SC-04): a fixed byte budget for the string cells of a buffered result (the group keys of a planned aggregation). Strings are read
/// in chunks through <see cref="DbDataReader.GetChars"/> and the read stops as soon as the per-value or the total budget is exceeded, so a
/// multi-gigabyte <c>nvarchar(max)</c> value is never buffered. Exceeding a budget throws <see cref="AggregationRowCapExceededException"/>
/// (fixed message, no value). Sizes count UTF-16 bytes (two per char), the in-memory size of the string. The budgets are constants, never request input.
/// </summary>
public sealed record ResultByteBudget(long MaxValueBytes, long MaxTotalBytes)
{
    private const int ChunkChars = 1024;

    /// <summary>16 KiB per group key and 32 MiB per result.</summary>
    public static ResultByteBudget GroupKeys { get; } = new(16 * 1024, 32L * 1024 * 1024);

    /// <summary>Reads one cell; string cells are counted against the budget, every other value is read as is.</summary>
    public object? ReadValue(DbDataReader reader, int ordinal, ref long totalBytes)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        if (reader.GetFieldType(ordinal) != typeof(string))
        {
            return reader.GetValue(ordinal);
        }

        long remainingTotal = Math.Max(0, MaxTotalBytes - totalBytes);
        long limitChars = Math.Min(MaxValueBytes, remainingTotal) / 2;
        var buffer = ArrayPool<char>.Shared.Rent(ChunkChars);
        try
        {
            var sb = new StringBuilder();
            while (sb.Length <= limitChars)
            {
                int want = (int)Math.Min(ChunkChars, limitChars - sb.Length + 1);
                long read = reader.GetChars(ordinal, sb.Length, buffer, 0, want);
                if (read <= 0)
                {
                    break;
                }

                sb.Append(buffer, 0, (int)read);
            }

            if (sb.Length > limitChars)
            {
                throw new AggregationRowCapExceededException();
            }

            totalBytes += sb.Length * 2L;
            return sb.ToString();
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }
}
