namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

using TrinoSqlEngine;

/// <summary>
/// Issue #107 (PLAN-GOV-107 D-WPC-3, SEC-RF107-18): pure helpers for the text budget of a statement with substituted key lists. The governed compiler
/// limits the emitted text to <c>MaxExpansionFactor * max(userSqlLength, 64)</c> characters; a padded <c>IN</c> list of a few thousand keys does not fit
/// the budget of a short statement. Everything here is a pure function of the user SQL length, the normalized factor and the shape of a list: it never
/// enters the cache key, never evicts or changes an entry, and a fallback it causes is never cached.
/// </summary>
public static class RowFilterKeySetTextBudget
{
    private const string ParameterPrefix = "__rls_ks";

    /// <summary>The compiler's output budget: <c>factor * max(userSqlLength, <see cref="CompileLimits.MinimumInputForExpansion"/>)</c> (same formula, same constant).</summary>
    public static long Limit(int userSqlLength, int maxExpansionFactor)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(userSqlLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxExpansionFactor, 1);
        return (long)maxExpansionFactor * Math.Max(userSqlLength, CompileLimits.MinimumInputForExpansion);
    }

    /// <summary>Characters all <c>IN</c> lists of one statement may take together: half of <see cref="Limit"/>; the other half is the reserve for the rewritten statement, tenant wrappers, masks and the remaining policy text.</summary>
    public static long KeyListBudget(int userSqlLength, int maxExpansionFactor) => Limit(userSqlLength, maxExpansionFactor) / 2;

    /// <summary>
    /// An <b>upper bound</b> of the emitted text of one <c>fk IN (...)</c> leaf with <paramref name="paddedCount"/> items (pinned against the real emitter
    /// for all dialects by test): per item the bind name length plus sigil and separator; per <c>IN</c> chunk (Oracle: one per 1,000 items joined by
    /// <c>OR</c>) twice the quoted column reference plus the list syntax.
    /// </summary>
    public static long EstimateListChars(TargetSqlDialect dialect, int siteOrdinal, int paddedCount, string foreignKeyColumn)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(siteOrdinal);
        ArgumentOutOfRangeException.ThrowIfNegative(paddedCount);
        ArgumentNullException.ThrowIfNull(foreignKeyColumn);
        if (paddedCount == 0)
        {
            return 5;   // 1 = 0
        }

        // sigil + "__rls_ks" + ordinal + "_" + index + ", " per item; the index digits are summed per decade, not per item.
        long perItem = paddedCount * (long)(1 + ParameterPrefix.Length + Digits(siteOrdinal) + 1 + 2) + IndexDigitSum(paddedCount);

        int chunkSize = ChunkSize(dialect);
        long chunks = (paddedCount + chunkSize - 1) / chunkSize;
        long perChunk = 2L * (RowFilterAliases.Target.Length + foreignKeyColumn.Length + 6) + 16;
        return perItem + chunks * perChunk;
    }

    /// <summary>Items per <c>IN</c> list: the dialect limit (Oracle 1,000), otherwise a single list.</summary>
    public static int ChunkSize(TargetSqlDialect dialect) => PolicyParameterNamer.InListLimit(dialect) is > 0 and var limit ? limit : int.MaxValue;

    /// <summary>Sum of the decimal digit counts of the indexes <c>0 .. count - 1</c> (closed form per decade; equals the naive per-item sum).</summary>
    internal static long IndexDigitSum(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        long total = 0;
        long lower = 0;
        long upper = 10;
        for (int digits = 1; lower < count; digits++)
        {
            total += (Math.Min(count, upper) - lower) * digits;
            lower = upper;
            upper *= 10;
        }

        return total;
    }

    private static int Digits(int value)
    {
        int digits = 1;
        for (long bound = 10; value >= bound; bound *= 10)
        {
            digits++;
        }

        return digits;
    }
}
