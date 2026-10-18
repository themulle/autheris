namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

using System.Globalization;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;

/// <summary>
/// SEC-RF107-07 (a)-(c), PLAN-GOV-107 A-9: converts the loaded primary key values of a dependent table to the type of the foreign key
/// column of the target table. The pair must be on the closed allowlist (<see cref="KeySetKeyTypes.IsBindablePair"/>, also fed with the
/// catalog collations); every value is converted with a checked conversion. A value that does not fit (overflow, longer than the declared
/// length, scale, precision, not a canonical uuid, wrong CLR type, null) refuses the <b>whole site</b>: keys are never truncated, rounded
/// or dropped. Output values are <see cref="long"/> (whole numbers), <see cref="decimal"/> (exact numerics) or <see cref="string"/> (text, uuid).
/// </summary>
public sealed class KeySetValueConverter
{
    private readonly KeyTypeClass _class;
    private readonly long _min;
    private readonly long _max;
    private readonly int _length;
    private readonly int _precision;

    private KeySetValueConverter(KeyTypeClass cls, SqlParameterType parameterType, long min, long max, int length, int precision)
    {
        _class = cls;
        ParameterType = parameterType;
        _min = min;
        _max = max;
        _length = length;
        _precision = precision;
    }

    /// <summary>The bind type of every converted key (the column type annotation of the policy predicate refines it at compile time).</summary>
    public SqlParameterType ParameterType { get; }

    /// <summary>The converter of a bindable pair, or <c>null</c> when the pair is not on the allowlist (the site stays <c>EXISTS</c>, reason <c>TypeNotBindable</c>).</summary>
    public static KeySetValueConverter? Create(
        TargetSqlDialect dialect, string? keyType, string? foreignKeyType, string? keyCollation = null, string? foreignKeyCollation = null)
    {
        if (!KeySetKeyTypes.IsBindablePair(dialect, keyType, foreignKeyType, keyCollation, foreignKeyCollation))
        {
            return null;
        }

        var fk = KeySetKeyTypes.Classify(dialect, foreignKeyType);
        switch (fk.Class)
        {
            case KeyTypeClass.WholeNumber:
                var (min, max) = Range(dialect, fk.Rank);
                return new KeySetValueConverter(fk.Class, SqlParameterType.Int64, min, max, 0, 0);
            case KeyTypeClass.ExactNumeric:
                return new KeySetValueConverter(fk.Class, SqlParameterType.Decimal, 0, 0, 0, Precision(dialect, foreignKeyType));
            case KeyTypeClass.NonUnicodeText or KeyTypeClass.UnicodeText:
                return TryFirstArgument(foreignKeyType, out int length)
                    ? new KeySetValueConverter(fk.Class, SqlParameterType.String, 0, 0, length, 0)
                    : null;
            case KeyTypeClass.Uuid:
                return new KeySetValueConverter(fk.Class, SqlParameterType.String, 0, 0, 0, 0);
            default:
                return null;
        }
    }

    /// <summary>Converts every value or none: on a refusal <paramref name="keys"/> is empty (no partial list).</summary>
    public bool TryConvertAll(IReadOnlyCollection<object?> raw, out object[] keys)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var result = new object[raw.Count];
        int i = 0;
        foreach (var value in raw)
        {
            if (!TryConvert(value, out var converted))
            {
                keys = [];
                return false;
            }

            result[i++] = converted;
        }

        keys = result;
        return true;
    }

    public bool TryConvert(object? raw, out object converted)
    {
        converted = null!;
        switch (_class)
        {
            case KeyTypeClass.WholeNumber:
                if (TryWhole(raw, out long whole) && whole >= _min && whole <= _max)
                {
                    converted = whole;
                    return true;
                }

                return false;
            case KeyTypeClass.ExactNumeric:
                if (TryDecimal(raw, out decimal exact) && exact == decimal.Truncate(exact) && FitsPrecision(exact))
                {
                    converted = exact / 1.000000000000000000000000000000000m;   // drops trailing zeros of the scale: 5.00 -> 5
                    return true;
                }

                return false;
            case KeyTypeClass.NonUnicodeText or KeyTypeClass.UnicodeText:
                if (raw is string text && text.Length <= _length)
                {
                    converted = text;
                    return true;
                }

                return false;
            case KeyTypeClass.Uuid:
                return TryUuid(raw, out converted);
            default:
                return false;
        }
    }

    private bool FitsPrecision(decimal value)
    {
        if (_precision >= 29)
        {
            return true;   // every decimal value fits
        }

        decimal limit = 1m;
        for (int i = 0; i < _precision; i++)
        {
            limit *= 10m;
        }

        return Math.Abs(value) < limit;
    }

    private static bool TryWhole(object? raw, out long value)
    {
        switch (raw)
        {
            case sbyte v: value = v; return true;
            case byte v: value = v; return true;
            case short v: value = v; return true;
            case ushort v: value = v; return true;
            case int v: value = v; return true;
            case uint v: value = v; return true;
            case long v: value = v; return true;
            case ulong v when v <= long.MaxValue: value = (long)v; return true;
            case decimal d when d == decimal.Truncate(d) && d >= long.MinValue && d <= long.MaxValue: value = (long)d; return true;
            default: value = 0; return false;
        }
    }

    private static bool TryDecimal(object? raw, out decimal value)
    {
        switch (raw)
        {
            case decimal d: value = d; return true;
            case ulong u: value = u; return true;
            default:
                if (TryWhole(raw, out long whole))
                {
                    value = whole;
                    return true;
                }

                value = 0;
                return false;
        }
    }

    private static bool TryUuid(object? raw, out object converted)
    {
        converted = null!;
        switch (raw)
        {
            case Guid guid:
                converted = guid.ToString("D", CultureInfo.InvariantCulture);
                return true;
            case string text when text.Length == 36 && Guid.TryParseExact(text, "D", out var parsed)
                && string.Equals(parsed.ToString("D", CultureInfo.InvariantCulture), text, StringComparison.Ordinal):
                converted = text;
                return true;
            default:
                return false;
        }
    }

    private static (long Min, long Max) Range(TargetSqlDialect dialect, int rank) => rank switch
    {
        1 => dialect == TargetSqlDialect.SqlServer ? (0, 255) : (sbyte.MinValue, sbyte.MaxValue),   // SQL Server tinyint is unsigned
        2 => (short.MinValue, short.MaxValue),
        4 => (int.MinValue, int.MaxValue),
        _ => (long.MinValue, long.MaxValue)
    };

    /// <summary>Declared precision of <c>numeric(p[, 0])</c>; SQL Server's <c>decimal</c> without precision is <c>(18, 0)</c>.</summary>
    private static int Precision(TargetSqlDialect dialect, string? type) =>
        TryFirstArgument(type, out int precision) ? precision : dialect == TargetSqlDialect.SqlServer ? 18 : 0;

    private static bool TryFirstArgument(string? type, out int value)
    {
        value = 0;
        if (type is null)
        {
            return false;
        }

        int open = type.IndexOf('(', StringComparison.Ordinal);
        if (open < 0)
        {
            return false;
        }

        int end = type.IndexOfAny([',', ')'], open + 1);
        return end > open + 1 && int.TryParse(type.AsSpan(open + 1, end - open - 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
    }
}
