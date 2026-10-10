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
        object? result = type switch
        {
            VirtualFilterKeyType.IntegerNumber => ToInteger(value),
            VirtualFilterKeyType.DecimalNumber => ToDecimal(value),
            VirtualFilterKeyType.Uuid => ToUuid(value),
            VirtualFilterKeyType.Text => ToText(value),
            _ => null
        };

        canonical = result!;
        return result != null;
    }

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

    private static object? ToInteger(object? value) => value switch
    {
        long l => l,
        int i => (long)i,
        short s => (long)s,
        byte b => (long)b,
        sbyte sb => (long)sb,
        ushort us => (long)us,
        uint ui => (long)ui,
        ulong ul when ul <= long.MaxValue => (long)ul,
        decimal m when decimal.Truncate(m) == m && m >= long.MinValue && m <= long.MaxValue => (long)m,
        double d => IntegerFromDouble(d),
        float f => IntegerFromDouble(f),
        string s when long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };

    private static object? IntegerFromDouble(double d) =>
        double.IsFinite(d) && Math.Floor(d) == d && Math.Abs(d) < MaxExactDouble ? (long)d : null;

    private static object? ToDecimal(object? value)
    {
        decimal? result = value switch
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
        return result is { } exact ? exact / 1.000000000000000000000000000000000m : null;
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

    private static object? ToUuid(object? value) => value switch
    {
        Guid g => g,
        string s when Guid.TryParse(s, out var parsed) => parsed,
        _ => null
    };

    private static object? ToText(object? value) => value switch
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
}
