namespace Autheris.Domain.Model;

using System;

/// <summary>
/// Overflow-safe time arithmetic for multi-year grant validity. Operator-configured limits have no compiled-in ceiling, so
/// additions saturate at the representable range instead of throwing.
/// </summary>
public static class GrantTimeMath
{
    /// <summary>Adds <paramref name="span"/> to <paramref name="start"/>; the result saturates at <see cref="DateTimeOffset.MaxValue"/>.</summary>
    public static DateTimeOffset AddSaturating(DateTimeOffset start, TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
        {
            return start;
        }

        var remaining = DateTimeOffset.MaxValue - start;
        return span >= remaining ? DateTimeOffset.MaxValue : start + span;
    }

    /// <summary>Converts a positive day count to a span; counts beyond the representable range saturate at <see cref="TimeSpan.MaxValue"/>.</summary>
    public static TimeSpan DaysToSpan(int days) =>
        days <= 0 ? TimeSpan.Zero : days >= TimeSpan.MaxValue.TotalDays ? TimeSpan.MaxValue : TimeSpan.FromDays(days);

    /// <summary>Smaller of two optional limits where <c>null</c> means unlimited.</summary>
    public static TimeSpan? MinLimit(TimeSpan? a, TimeSpan? b) =>
        a is null ? b : b is null ? a : (a.Value <= b.Value ? a : b);
}
