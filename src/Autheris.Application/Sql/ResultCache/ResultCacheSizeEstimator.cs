namespace Autheris.Application.Sql.ResultCache;

using System;

/// <summary>Pessimistic byte estimate of a recorded result. A value of a type that cannot be replayed faithfully is not accepted.</summary>
public static class ResultCacheSizeEstimator
{
    private const long RowOverhead = 64;
    private const long CellOverhead = 16;
    private const long ObjectOverhead = 24;
    private const long FixedValue = 32;

    /// <summary>The estimated size of one row with <paramref name="columns"/> cells, before the values.</summary>
    public static long RowBaseBytes(int columns) => RowOverhead + (CellOverhead * columns);

    /// <summary>Adds the estimate of <paramref name="value"/> to <paramref name="bytes"/>; false for an unsupported type.</summary>
    public static bool TryAdd(object? value, ref long bytes)
    {
        switch (value)
        {
            case null:
            case DBNull:
                return true;
            case string s:
                bytes += ObjectOverhead + (2L * s.Length);
                return true;
            case byte[] b:
                bytes += ObjectOverhead + b.Length;
                return true;
            case bool or byte or sbyte or short or ushort or int or uint or long or ulong or char or float or double or decimal
                or DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan or Guid:
                bytes += FixedValue;
                return true;
            default:
                return false;
        }
    }
}
