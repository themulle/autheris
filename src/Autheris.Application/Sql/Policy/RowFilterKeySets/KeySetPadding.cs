namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

using System.Numerics;

/// <summary>
/// IN-list padding buckets (PLAN-GOV-107 A-10): a list of <c>count</c> keys has <c>min(nextPow2(count), maxKeys)</c> items, so that the
/// shape of a statement (and with it the template-cache entry) changes only per bucket, not per key count.
/// </summary>
public static class KeySetPadding
{
    /// <summary>0 for an empty set (the filter becomes <c>1 = 0</c>). A count above <paramref name="maxKeys"/> is an overflow the caller handles; keys are never truncated.</summary>
    public static int Bucket(int count, int maxKeys)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxKeys, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, maxKeys);
        if (count == 0)
        {
            return 0;
        }

        long next = BitOperations.RoundUpToPowerOf2((uint)count);
        return (int)Math.Min(next, maxKeys);
    }
}
