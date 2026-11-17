using System.Globalization;

namespace TrinoSqlEngine.Numerics;

/// <summary>
/// Converts double and float parameter values to <see cref="decimal"/> rounded to 15 and 7 significant digits (the pre-.NET 11
/// result of <c>Convert.ToDecimal</c>): .NET 11 converts exactly, so <c>0.1d</c> would bind as 0.1000000000000000055...
/// </summary>
internal static class BinaryFloatingPointDecimal
{
    internal static decimal ToDecimal(object value) => value switch
    {
        double d => Round((decimal)d, 15),
        float f => Round((decimal)f, 7),
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
    };

    private static decimal Round(decimal exact, int digits)
    {
        if (exact == 0m)
        {
            return 0m;
        }

        int scale = digits - 1 - (int)Math.Floor(Math.Log10((double)Math.Abs(exact)));
        if (scale < 0)
        {
            return exact;
        }

        return decimal.Round(exact, Math.Min(scale, 28), MidpointRounding.AwayFromZero) / 1.0000000000000000000000000000m;
    }
}
