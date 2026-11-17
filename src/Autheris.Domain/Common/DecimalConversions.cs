using System.Globalization;

namespace Autheris.Domain.Common;

/// <summary>
/// Conversion of binary floating point values to <see cref="decimal"/> with the pre-.NET 11 semantics: the result is the value
/// rounded to the significant digits a <c>double</c> (15) or <c>float</c> (7) can represent, so <c>0.1d</c> becomes <c>0.1m</c>.
/// .NET 11 converts exactly (<c>(decimal)0.1d</c> is 0.1000000000000000055...), which breaks equality against decimal literals,
/// thresholds, key sets and filter values that were written as decimal text.
/// </summary>
public static class DecimalConversions
{
    private const int DoubleDigits = 15;
    private const int SingleDigits = 7;

    /// <summary>Converts a finite double; throws <see cref="OverflowException"/> like the cast when it is out of range.</summary>
    public static decimal FromDouble(double value) => RoundToSignificantDigits((decimal)value, DoubleDigits);

    /// <summary>Converts a finite float; throws <see cref="OverflowException"/> like the cast when it is out of range.</summary>
    public static decimal FromSingle(float value) => RoundToSignificantDigits((decimal)value, SingleDigits);

    /// <summary>Like <see cref="Convert.ToDecimal(object?, IFormatProvider?)"/> with the invariant culture, using the rounding above for double and float.</summary>
    public static decimal ToDecimal(object? value) => value switch
    {
        double d => FromDouble(d),
        float f => FromSingle(f),
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
    };

    private static decimal RoundToSignificantDigits(decimal exact, int digits)
    {
        if (exact == 0m)
        {
            return 0m;
        }

        int magnitude = (int)Math.Floor(Math.Log10((double)Math.Abs(exact)));
        int scale = digits - 1 - magnitude;
        if (scale < 0)
        {
            return exact;
        }

        decimal rounded = decimal.Round(exact, Math.Min(scale, 28), MidpointRounding.AwayFromZero);

        // Dividing by 1.000...0m drops the trailing zeros the rounding added (0.800000000000000m -> 0.8m).
        return rounded / 1.0000000000000000000000000000m;
    }
}
