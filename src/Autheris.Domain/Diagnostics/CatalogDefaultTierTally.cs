namespace Autheris.Domain.Diagnostics;

/// <summary>
/// Issue #111 (A-9): process-wide tally of tables that received a non-Hidden default tier when they were created. The repositories note each
/// insert; a hosted service turns the tally into one aggregated audit entry (count only, no table names). A rolled-back creation may be noted
/// anyway; the entry reports "up to" that many.
/// </summary>
public static class CatalogDefaultTierTally
{
    private static long s_count;

    public static void Note() => Interlocked.Increment(ref s_count);

    public static long TakeAndReset() => Interlocked.Exchange(ref s_count, 0);
}
