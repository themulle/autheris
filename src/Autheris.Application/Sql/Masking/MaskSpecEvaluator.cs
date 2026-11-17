namespace Autheris.Application.Sql.Masking;

using Autheris.Domain.Common;
using System;
using System.Globalization;
using System.Linq;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// Evaluates a typed <see cref="MaskSpec"/> on an in-memory value so non-SQL and SQL masking share one definition
/// of each unkeyed mask kind, its arguments, and its null handling. Keyed or approximate kinds (<see cref="MaskKind.Hmac"/>,
/// <see cref="MaskKind.Tokenize"/>, <see cref="MaskKind.GeoJitter"/>) are rejected here (fail closed).
/// </summary>
public static class MaskSpecEvaluator
{
    /// <summary>Length of the mask the SQL emitters produce when the value is not longer than the revealed parts.</summary>
    internal const int ShortValueMaskLength = 5;

    /// <param name="spec">The typed mask.</param>
    /// <param name="value">The clear value; null and <see cref="DBNull"/> stay null for every kind.</param>
    /// <param name="canonicalText">The culture-invariant text of <paramref name="value"/> (used by text-based kinds).</param>
    /// <exception cref="NotSupportedException">The kind is not evaluable without a key (the caller must deny).</exception>
    public static object? Evaluate(MaskSpec spec, object? value, string canonicalText)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(canonicalText);
        if (value is null or DBNull)
        {
            return null;
        }

        switch (spec.Kind)
        {
            case MaskKind.Nullify:
                return null;

            case MaskKind.Redact:
                // Numeric, temporal and boolean columns redact to a typed NULL (distinguishable from a valid zero or epoch).
                return IsNumericTemporalOrBoolean(value) ? null : Text(spec, spec.Arguments.Constant);

            case MaskKind.Constant:
                return Text(spec, spec.Arguments.Constant);

            case MaskKind.PartialMask:
                return PartialMask(spec, canonicalText);

            case MaskKind.TruncateDate:
                return TruncateDate(spec, value);

            case MaskKind.BucketNumber:
                return BucketNumber(spec, value);

            case MaskKind.RoundNumber:
                return RoundNumber(spec, value);

            case MaskKind.EmailDomain:
                return EmailDomain(canonicalText);

            default:
                throw new NotSupportedException($"The mask kind {spec.Kind} cannot be evaluated without a key.");
        }
    }

    private static bool IsNumericTemporalOrBoolean(object value) =>
        value is int or long or short or sbyte or byte or uint or ulong or ushort or decimal or double or float
            or DateTime or DateTimeOffset or DateOnly or TimeOnly or bool;

    private static string Text(MaskSpec spec, PolicyParameterExpression? parameter)
    {
        if (parameter is null || !spec.Parameters.TryGetValue(parameter.Name, out var bound) || bound.Value is not string text)
        {
            throw new InvalidOperationException($"The {spec.Kind} mask has no bound text constant.");
        }

        return text;
    }

    private static int Count(MaskSpec spec, PolicyParameterExpression? parameter, bool allowNegative = false)
    {
        if (parameter is null || !spec.Parameters.TryGetValue(parameter.Name, out var bound) || bound.Value is not int count)
        {
            throw new InvalidOperationException($"The {spec.Kind} mask has no bound integer parameter.");
        }

        return allowNegative ? count : Math.Max(0, count);
    }

    private static decimal DecimalArg(MaskSpec spec, PolicyParameterExpression? parameter)
    {
        if (parameter is null || !spec.Parameters.TryGetValue(parameter.Name, out var bound) || bound.Value is null)
        {
            throw new InvalidOperationException($"The {spec.Kind} mask has no bound decimal parameter.");
        }

        decimal d = DecimalConversions.ToDecimal(bound.Value);
        if (d <= 0m)
        {
            throw new InvalidOperationException($"The {spec.Kind} mask parameter must be positive.");
        }

        return d;
    }

    private static object TruncateDate(MaskSpec spec, object value)
    {
        var unit = spec.Arguments.TruncateUnit
            ?? throw new InvalidOperationException("TruncateDate mask requires TruncateUnit.");
        return value switch
        {
            DateOnly d => TruncateDateOnly(d, unit),
            DateTime dt => TruncateDateTime(dt, unit),
            DateTimeOffset dto => new DateTimeOffset(TruncateDateTime(dto.DateTime, unit), dto.Offset),
            string s when DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dOnly) =>
                TruncateDateOnly(dOnly, unit).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dtoParsed) =>
                new DateTimeOffset(TruncateDateTime(dtoParsed.UtcDateTime, unit), TimeSpan.Zero),
            _ => throw new InvalidOperationException($"TruncateDate mask cannot be applied to value of type '{value.GetType().Name}'.")
        };
    }

    private static DateOnly TruncateDateOnly(DateOnly d, MaskDateTruncateUnit unit) => unit switch
    {
        MaskDateTruncateUnit.Year => new DateOnly(d.Year, 1, 1),
        MaskDateTruncateUnit.Month => new DateOnly(d.Year, d.Month, 1),
        MaskDateTruncateUnit.Day => d,
        _ => throw new InvalidOperationException($"Unsupported TruncateUnit '{unit}'.")
    };

    private static DateTime TruncateDateTime(DateTime dt, MaskDateTruncateUnit unit) => unit switch
    {
        MaskDateTruncateUnit.Year => new DateTime(dt.Year, 1, 1, 0, 0, 0, dt.Kind),
        MaskDateTruncateUnit.Month => new DateTime(dt.Year, dt.Month, 1, 0, 0, 0, dt.Kind),
        MaskDateTruncateUnit.Day => new DateTime(dt.Year, dt.Month, dt.Day, 0, 0, 0, dt.Kind),
        _ => throw new InvalidOperationException($"Unsupported TruncateUnit '{unit}'.")
    };

    private static object BucketNumber(MaskSpec spec, object value)
    {
        decimal width = DecimalArg(spec, spec.Arguments.BucketWidth);
        if (value is double dbl)
        {
            double w = (double)width;
            return Math.Floor(dbl / w) * w;
        }

        if (value is float flt)
        {
            double w = (double)width;
            return (float)(Math.Floor(flt / w) * w);
        }

        decimal d = DecimalConversions.ToDecimal(value);
        decimal bucket = Math.Floor(d / width) * width;
        return ConvertNumericResult(value, bucket);
    }

    private static object RoundNumber(MaskSpec spec, object value)
    {
        int digits = Count(spec, spec.Arguments.RoundDigits, allowNegative: true);
        if (value is double dbl)
        {
            if (digits >= 0)
            {
                return Math.Round(dbl, Math.Min(digits, 15), MidpointRounding.AwayFromZero);
            }

            double factor = Math.Pow(10, -digits);
            return Math.Round(dbl / factor, 0, MidpointRounding.AwayFromZero) * factor;
        }

        if (value is float flt)
        {
            if (digits >= 0)
            {
                return (float)Math.Round(flt, Math.Min(digits, 15), MidpointRounding.AwayFromZero);
            }

            double factor = Math.Pow(10, -digits);
            return (float)(Math.Round(flt / factor, 0, MidpointRounding.AwayFromZero) * factor);
        }

        decimal d = DecimalConversions.ToDecimal(value);
        decimal rounded = digits >= 0
            ? Math.Round(d, Math.Min(digits, 28), MidpointRounding.AwayFromZero)
            : Math.Round(d / MaskFunctionCatalog.Pow10Decimal(-digits), 0, MidpointRounding.AwayFromZero) * MaskFunctionCatalog.Pow10Decimal(-digits);
        return ConvertNumericResult(value, rounded);
    }

    private static object ConvertNumericResult(object original, decimal result) => original switch
    {
        int => decimal.ToInt32(decimal.Truncate(result)),
        long => decimal.ToInt64(decimal.Truncate(result)),
        short => decimal.ToInt16(decimal.Truncate(result)),
        sbyte => decimal.ToSByte(decimal.Truncate(result)),
        byte => decimal.ToByte(decimal.Truncate(result)),
        uint => decimal.ToUInt32(decimal.Truncate(result)),
        ulong => decimal.ToUInt64(decimal.Truncate(result)),
        ushort => decimal.ToUInt16(decimal.Truncate(result)),
        _ => result
    };

    private static string EmailDomain(string text)
    {
        int at = text.IndexOf('@', StringComparison.Ordinal);
        if (at > 0 && at < text.Length - 1)
        {
            return "***@" + text[(at + 1)..];
        }

        return "***@***";
    }

    // The SQL emitters: length <= prefix + suffix gives five mask characters; otherwise prefix, one mask character per hidden
    // character, suffix. In memory the length is counted in Unicode scalar values, so a surrogate pair is never split.
    private static string PartialMask(MaskSpec spec, string text)
    {
        int prefixLength = Count(spec, spec.Arguments.KeepPrefix);
        int suffixLength = Count(spec, spec.Arguments.KeepSuffix);
        var maskText = Text(spec, spec.Arguments.MaskChar);
        char maskChar = maskText.Length > 0 ? maskText[0] : '*';

        var runes = text.EnumerateRunes().ToArray();
        if (runes.Length <= prefixLength + suffixLength)
        {
            return new string(maskChar, ShortValueMaskLength);
        }

        var prefix = string.Concat(runes.Take(prefixLength));
        var suffix = suffixLength > 0 ? string.Concat(runes.TakeLast(suffixLength)) : string.Empty;
        return prefix + new string(maskChar, runes.Length - prefixLength - suffixLength) + suffix;
    }
}
