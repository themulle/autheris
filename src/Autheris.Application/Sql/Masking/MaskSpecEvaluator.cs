namespace Autheris.Application.Sql.Masking;

using System;
using System.Linq;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// WP-ARC-B-04: evaluates a typed <see cref="MaskSpec"/> on an in-memory value, so a column masked in memory (HTTP and other non-SQL
/// sources) and a column masked by the dialect generator in SQL share one definition of the mask kind, its arguments and its null
/// handling. Covers the kinds that need no key: <see cref="MaskKind.Nullify"/>, <see cref="MaskKind.Redact"/>,
/// <see cref="MaskKind.Constant"/> and <see cref="MaskKind.PartialMask"/>. Keyed or numeric kinds (<see cref="MaskKind.Hmac"/>,
/// <see cref="MaskKind.GeoJitter"/>) are evaluated by the provider that owns the key and are rejected here (fail closed).
/// </summary>
public static class MaskSpecEvaluator
{
    /// <summary>Length of the mask the SQL emitters produce when the value is not longer than the revealed parts.</summary>
    internal const int ShortValueMaskLength = 5;

    /// <param name="spec">The typed mask.</param>
    /// <param name="value">The clear value; null and <see cref="DBNull"/> stay null for every kind.</param>
    /// <param name="canonicalText">The culture-invariant text of <paramref name="value"/> (used by the text based kinds).</param>
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

    private static int Count(MaskSpec spec, PolicyParameterExpression? parameter)
    {
        if (parameter is null || !spec.Parameters.TryGetValue(parameter.Name, out var bound) || bound.Value is not int count)
        {
            throw new InvalidOperationException($"The {spec.Kind} mask has no bound count.");
        }

        return Math.Max(0, count);
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
