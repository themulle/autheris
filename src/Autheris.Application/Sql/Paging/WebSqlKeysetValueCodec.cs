namespace Autheris.Application.Sql.Paging;

using System;
using System.Globalization;

/// <summary>
/// Encodes and decodes keyset continuation key values as <c>tag:value</c> strings. Tags are allowlisted,
/// formatting is culture independent and decoding only accepts the exact canonical form produced by encoding.
/// </summary>
internal static class WebSqlKeysetValueCodec
{
    public const int MaxEntryLength = 256;

    private const NumberStyles IntegerStyle = NumberStyles.AllowLeadingSign;
    private const NumberStyles DecimalStyle = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    public static bool TryEncode(object? value, out string encoded)
    {
        encoded = string.Empty;
        string? result = value switch
        {
            byte b => "u1:" + b.ToString(CultureInfo.InvariantCulture),
            short s => "i2:" + s.ToString(CultureInfo.InvariantCulture),
            int i => "i4:" + i.ToString(CultureInfo.InvariantCulture),
            long l => "i8:" + l.ToString(CultureInfo.InvariantCulture),
            decimal m => "dec:" + m.ToString(CultureInfo.InvariantCulture),
            string str => "s:" + str,
            Guid g => "g:" + g.ToString("D", CultureInfo.InvariantCulture),
            DateOnly d => "d:" + d.DayNumber.ToString(CultureInfo.InvariantCulture),
            DateTime dt => "dt:" + dt.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + KindCode(dt.Kind),
            DateTimeOffset dto => EncodeOffset(dto),
            _ => null
        };

        if (result is null || result.Length > MaxEntryLength)
        {
            return false;
        }

        encoded = result;
        return true;
    }

    public static bool TryDecode(string? encoded, out object value)
    {
        value = null!;
        if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxEntryLength)
        {
            return false;
        }

        int sep = encoded.IndexOf(':', StringComparison.Ordinal);
        if (sep <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> tag = encoded.AsSpan(0, sep);
        string body = encoded[(sep + 1)..];

        switch (tag)
        {
            case "u1":
                if (byte.TryParse(body, IntegerStyle, CultureInfo.InvariantCulture, out var b)
                    && b.ToString(CultureInfo.InvariantCulture) == body)
                {
                    value = b;
                    return true;
                }
                return false;
            case "i2":
                if (short.TryParse(body, IntegerStyle, CultureInfo.InvariantCulture, out var s)
                    && s.ToString(CultureInfo.InvariantCulture) == body)
                {
                    value = s;
                    return true;
                }
                return false;
            case "i4":
                if (int.TryParse(body, IntegerStyle, CultureInfo.InvariantCulture, out var i)
                    && i.ToString(CultureInfo.InvariantCulture) == body)
                {
                    value = i;
                    return true;
                }
                return false;
            case "i8":
                if (long.TryParse(body, IntegerStyle, CultureInfo.InvariantCulture, out var l)
                    && l.ToString(CultureInfo.InvariantCulture) == body)
                {
                    value = l;
                    return true;
                }
                return false;
            case "dec":
                if (decimal.TryParse(body, DecimalStyle, CultureInfo.InvariantCulture, out var m)
                    && m.ToString(CultureInfo.InvariantCulture) == body)
                {
                    value = m;
                    return true;
                }
                return false;
            case "s":
                value = body;
                return true;
            case "g":
                if (Guid.TryParseExact(body, "D", out var g) && g.ToString("D", CultureInfo.InvariantCulture) == body)
                {
                    value = g;
                    return true;
                }
                return false;
            case "d":
                if (int.TryParse(body, IntegerStyle, CultureInfo.InvariantCulture, out var day)
                    && day.ToString(CultureInfo.InvariantCulture) == body
                    && day >= DateOnly.MinValue.DayNumber && day <= DateOnly.MaxValue.DayNumber)
                {
                    value = DateOnly.FromDayNumber(day);
                    return true;
                }
                return false;
            case "dt":
                return TryDecodeDateTime(body, out value);
            case "dto":
                return TryDecodeDateTimeOffset(body, out value);
            default:
                return false;
        }
    }

    private static string? EncodeOffset(DateTimeOffset dto)
    {
        if (dto.Offset.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            return null;
        }

        return "dto:" + dto.Ticks.ToString(CultureInfo.InvariantCulture) + ":"
            + ((long)dto.Offset.TotalMinutes).ToString(CultureInfo.InvariantCulture);
    }

    private static string KindCode(DateTimeKind kind) => kind switch
    {
        DateTimeKind.Utc => "U",
        DateTimeKind.Local => "L",
        _ => "N"
    };

    private static bool TryDecodeDateTime(string body, out object value)
    {
        value = null!;
        int sep = body.IndexOf(':', StringComparison.Ordinal);
        if (sep <= 0 || !TryParseTicks(body.AsSpan(0, sep), out long ticks))
        {
            return false;
        }

        DateTimeKind kind;
        switch (body.AsSpan(sep + 1))
        {
            case "U": kind = DateTimeKind.Utc; break;
            case "L": kind = DateTimeKind.Local; break;
            case "N": kind = DateTimeKind.Unspecified; break;
            default: return false;
        }

        value = new DateTime(ticks, kind);
        return true;
    }

    private static bool TryDecodeDateTimeOffset(string body, out object value)
    {
        value = null!;
        int sep = body.IndexOf(':', StringComparison.Ordinal);
        if (sep <= 0 || !TryParseTicks(body.AsSpan(0, sep), out long ticks))
        {
            return false;
        }

        string minutesText = body[(sep + 1)..];
        if (!int.TryParse(minutesText, IntegerStyle, CultureInfo.InvariantCulture, out int minutes)
            || minutes.ToString(CultureInfo.InvariantCulture) != minutesText
            || minutes is < -840 or > 840)
        {
            return false;
        }

        try
        {
            value = new DateTimeOffset(ticks, TimeSpan.FromMinutes(minutes));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryParseTicks(ReadOnlySpan<char> text, out long ticks)
    {
        return long.TryParse(text, IntegerStyle, CultureInfo.InvariantCulture, out ticks)
            && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
            && ticks.ToString(CultureInfo.InvariantCulture).AsSpan().SequenceEqual(text);
    }
}
