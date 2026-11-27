namespace TrinoSqlEngine.Ast.Emit;

using System;
using System.Globalization;

/// <summary>Checks a client value against the family its slot requires (numeric, temporal, boolean); a null value always conforms.</summary>
internal static class ClientBindFamilyCheck
{
    private static readonly string[] DateTimeFormats = ["yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF"];

    private const int MaxTemporalLength = 40;

    private static readonly string[] OffsetFormats =
    [
        "yyyy-MM-ddTHH:mm:sszzz", "yyyy-MM-ddTHH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd HH:mm:sszzz", "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz"
    ];

    private static readonly string[] UtcFormats =
    [
        "yyyy-MM-ddTHH:mm:ss'Z'", "yyyy-MM-ddTHH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd HH:mm:ss'Z'", "yyyy-MM-dd HH:mm:ss.FFFFFFF'Z'"
    ];

    public static bool Conforms(BindFamily family, object? value) => TryConform(family, value, ColumnTemporalKind.Unknown, out _);

    /// <summary>
    /// Checks the value against the slot family and, for a temporal text, converts it to a typed value (<see cref="DateOnly"/>, an
    /// unspecified <see cref="DateTime"/>, or UTC), so the database never converts text under a row policy. Parsing is strict and
    /// culture-free; a text without an offset never assumes local time.
    /// </summary>
    public static bool TryConform(BindFamily family, object? value, ColumnTemporalKind temporalKind, out object? conformed)
    {
        conformed = value;
        if (value is null or DBNull) return true;
        var invariant = CultureInfo.InvariantCulture;
        switch (family)
        {
            case BindFamily.Numeric:
                return value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
                    || (value is string n && decimal.TryParse(n, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, invariant, out _));
            case BindFamily.Temporal:
                if (value is DateTime or DateTimeOffset or DateOnly or TimeOnly or TimeSpan) return true;
                return value is string t && TryParseTemporal(t, temporalKind, out conformed);
            case BindFamily.Boolean:
                return value is bool;
            // A number bound against a text column makes SQL Server and Oracle convert the column, which raises on non-numeric text.
            case BindFamily.Text:
                return value is string or Guid or char;
            default:
                return false;
        }
    }

    private static bool TryParseTemporal(string text, ColumnTemporalKind kind, out object? typed)
    {
        typed = null;
        if (text.Length is 0 or > MaxTemporalLength) return false;
        var invariant = CultureInfo.InvariantCulture;
        if (text.Length == 10)
        {
            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", invariant, DateTimeStyles.None, out var date)) return false;
            typed = date;
            return true;
        }

        if (DateTime.TryParseExact(text, DateTimeFormats, invariant, DateTimeStyles.None, out var plain))
        {
            typed = DateTime.SpecifyKind(plain, DateTimeKind.Unspecified);
            return true;
        }

        // zzz alone also accepts +HHmm; only the colon form is part of the contract.
        if (text[^1] != 'Z' && (text.Length < 6 || text[^3] != ':' || text[^6] is not ('+' or '-'))) return false;
        if (!DateTimeOffset.TryParseExact(text, OffsetFormats, invariant, DateTimeStyles.None, out var offset)
            && !DateTimeOffset.TryParseExact(text, UtcFormats, invariant, DateTimeStyles.AssumeUniversal, out offset))
        {
            return false;
        }

        if (offset.Offset > TimeSpan.FromHours(14) || offset.Offset < TimeSpan.FromHours(-14)) return false;
        var utc = offset.UtcDateTime;
        // Boxed separately: a conditional of DateTime and DateTimeOffset would convert the DateTime to DateTimeOffset.
        if (kind == ColumnTemporalKind.WithoutZone)
        {
            typed = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
        }
        else
        {
            typed = new DateTimeOffset(utc, TimeSpan.Zero);
        }

        return true;
    }
}
