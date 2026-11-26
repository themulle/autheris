namespace Autheris.Application.Sql.ResultCache;

using System;

/// <summary>
/// Memory held for a recording that is in progress. Growing it can fail when the global or the per-user budget is exhausted;
/// the recording then stops while the response keeps streaming. Disposing releases everything that was granted.
/// </summary>
public interface IResultCacheReservation : IDisposable
{
    bool TryGrow(long bytes);
}

/// <summary>Tracks rows and bytes of one recording against the entry caps and the reservation.</summary>
internal sealed class ResultCacheRowBudget(int maxRows, long maxBytes, IResultCacheReservation? reservation)
{
    private const long ReservationChunk = 64 * 1024;
    private long _reserved;

    public long Bytes { get; private set; }

    public int Rows { get; private set; }

    /// <summary>Accounts for <paramref name="row"/>. Null when it fits; otherwise the reason it cannot be recorded.</summary>
    public string? TryAdd(object?[] row)
    {
        if (Rows >= maxRows)
        {
            return WebSqlResultCacheReasons.TooLarge;
        }

        var size = Bytes + ResultCacheSizeEstimator.RowBaseBytes(row.Length);
        foreach (var value in row)
        {
            if (!ResultCacheSizeEstimator.TryAdd(value, ref size))
            {
                return WebSqlResultCacheReasons.UnsupportedValueType;
            }
        }

        if (size > maxBytes)
        {
            return WebSqlResultCacheReasons.TooLarge;
        }

        if (reservation is not null && size > _reserved)
        {
            var grow = Math.Min(Math.Max(size - _reserved, ReservationChunk), maxBytes - _reserved);
            if (grow < size - _reserved || !reservation.TryGrow(grow))
            {
                return WebSqlResultCacheReasons.Incomplete;
            }

            _reserved += grow;
        }

        Bytes = size;
        Rows++;
        return null;
    }
}
