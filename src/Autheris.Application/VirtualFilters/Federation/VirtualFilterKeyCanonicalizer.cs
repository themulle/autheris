namespace Autheris.Application.VirtualFilters.Federation;

using System;
using System.Globalization;
using Autheris.Domain.Exceptions;

/// <summary>Canonical CLR type of a Virtual Filter key component, chosen by the catalog type of the protected column.</summary>
public enum VirtualFilterKeyType
{
    /// <summary>Integer family; canonical <see cref="long"/>.</summary>
    IntegerNumber = 1,

    /// <summary><c>decimal</c> / <c>numeric</c>; canonical <see cref="decimal"/> without trailing zeros.</summary>
    DecimalNumber = 2,

    /// <summary><c>uuid</c> / <c>uniqueidentifier</c>; canonical <see cref="Guid"/>.</summary>
    Uuid = 3,

    /// <summary>Text family; canonical <see cref="string"/>, compared ordinally (case-sensitive).</summary>
    Text = 4
}

/// <summary>
/// F-GOV-14 (M2, SEC-18): converts key values from the key source and from upstream rows into one canonical value per
/// key type. A value that cannot be converted exactly is rejected (the key is dropped, the row never matches). Floating
/// point values only count when they prove their value: integral and below 2^53 for integers, exact round trip for
/// decimals. Case-insensitive columns are not supported (always ordinal, the stricter choice).
/// </summary>
public static class VirtualFilterKeyCanonicalizer
{
    /// <summary>2^53: from here on a double cannot distinguish neighbouring integers.</summary>
    private const double MaxExactDouble = 9007199254740992d;

    /// <summary>The key type for a catalog data type; any other type denies with <c>VF_KEY_TYPE_MISMATCH</c>.</summary>
    public static VirtualFilterKeyType ForColumnType(string? dataType, string column)
    {
        var normalized = Normalize(dataType);
        return normalized switch
        {
            "tinyint" or "smallint" or "int" or "integer" or "bigint" or "int2" or "int4" or "int8" or "int16" or "int32" or "int64"
                or "long" or "short" or "smallserial" or "serial" or "bigserial" => VirtualFilterKeyType.IntegerNumber,
            "decimal" or "numeric" or "number" or "dec" => VirtualFilterKeyType.DecimalNumber,
            "uuid" or "uniqueidentifier" or "guid" => VirtualFilterKeyType.Uuid,
            "varchar" or "nvarchar" or "char" or "nchar" or "text" or "ntext" or "string" or "character varying" or "character"
                or "bpchar" or "varchar2" or "nvarchar2" => VirtualFilterKeyType.Text,
            _ => throw new GatewaySecurityException(
                $"The Virtual Filter key column '{column}' has the type '{dataType}', which cannot be compared as a key.",
                VirtualFilterErrorCodes.KeyTypeMismatch)
        };
    }

    /// <summary>Converts <paramref name="value"/> to the canonical value of <paramref name="type"/>; false when not exact.</summary>
    public static bool TryCanonicalize(VirtualFilterKeyType type, object? value, out object canonical)
    {
        object? result = null;
        switch (type)
        {
            case VirtualFilterKeyType.IntegerNumber:
                result = TryGetInteger(value, out var integer) ? integer : null;
                break;
            case VirtualFilterKeyType.DecimalNumber:
                result = TryGetDecimal(value, out var number) ? number : null;
                break;
            case VirtualFilterKeyType.Uuid:
                result = TryGetUuid(value, out var uuid) ? uuid : null;
                break;
            case VirtualFilterKeyType.Text:
                result = TryGetText(value, out var text) ? text : null;
                break;
        }

        canonical = result!;
        return result != null;
    }

    /// <summary>The invariant text of a canonical key component, as it is sent to an upstream (pushdown values, path keys).</summary>
    internal static string FormatCanonical(object canonical) => canonical switch
    {
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        Guid g => g.ToString("D", CultureInfo.InvariantCulture),
        _ => Convert.ToString(canonical, CultureInfo.InvariantCulture) ?? string.Empty
    };

    private static string Normalize(string? dataType)
    {
        var text = (dataType ?? string.Empty).Trim().ToLowerInvariant();
        int paren = text.IndexOf('(');
        if (paren >= 0)
        {
            text = text[..paren].TrimEnd();
        }

        return text;
    }

    // The typed variants below are the single implementation of each conversion; they do not box, so the hot per-row
    // probe of the single-column enforcement path does not allocate (CR-08).

    internal static bool TryGetInteger(object? value, out long result)
    {
        switch (value)
        {
            case long l: result = l; return true;
            case int i: result = i; return true;
            case short s: result = s; return true;
            case byte b: result = b; return true;
            case sbyte sb: result = sb; return true;
            case ushort us: result = us; return true;
            case uint ui: result = ui; return true;
            case ulong ul when ul <= long.MaxValue: result = (long)ul; return true;
            case decimal m when decimal.Truncate(m) == m && m >= long.MinValue && m <= long.MaxValue: result = (long)m; return true;
            case double d: return IntegerFromDouble(d, out result);
            case float f: return IntegerFromDouble(f, out result);
            case string s when long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed): result = parsed; return true;
            default: result = 0; return false;
        }
    }

    private static bool IntegerFromDouble(double d, out long result)
    {
        if (double.IsFinite(d) && Math.Floor(d) == d && Math.Abs(d) < MaxExactDouble)
        {
            result = (long)d;
            return true;
        }

        result = 0;
        return false;
    }

    internal static bool TryGetDecimal(object? value, out decimal result)
    {
        decimal? exact = value switch
        {
            decimal m => m,
            long l => l,
            int i => i,
            short s => s,
            byte b => b,
            sbyte sb => sb,
            ushort us => us,
            uint ui => ui,
            ulong ul => ul,
            double d => DecimalFromDouble(d),
            float f => DecimalFromDouble(f),
            string s when decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };

        // Dividing by 1.000...0 removes trailing zeros: 12.50 and 12.5 become the same canonical value.
        result = exact is { } number ? number / 1.000000000000000000000000000000000m : 0;
        return exact != null;
    }

    private static decimal? DecimalFromDouble(double d)
    {
        if (!double.IsFinite(d) || Math.Abs(d) >= (double)decimal.MaxValue)
        {
            return null;
        }

        decimal converted;
        try
        {
            converted = (decimal)d;
        }
        catch (OverflowException)
        {
            return null;
        }

        return (double)converted == d ? converted : null;
    }

    internal static bool TryGetUuid(object? value, out Guid result)
    {
        switch (value)
        {
            case Guid g: result = g; return true;
            case string s when Guid.TryParse(s, out var parsed): result = parsed; return true;
            default: result = default; return false;
        }
    }

    internal static bool TryGetText(object? value, out string result)
    {
        string? text = value switch
        {
            string s => s,
            long l => l.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            short s => s.ToString(CultureInfo.InvariantCulture),
            byte b => b.ToString(CultureInfo.InvariantCulture),
            sbyte sb => sb.ToString(CultureInfo.InvariantCulture),
            ushort us => us.ToString(CultureInfo.InvariantCulture),
            uint ui => ui.ToString(CultureInfo.InvariantCulture),
            ulong ul => ul.ToString(CultureInfo.InvariantCulture),
            _ => null
        };

        result = text!;
        return text != null;
    }
}
