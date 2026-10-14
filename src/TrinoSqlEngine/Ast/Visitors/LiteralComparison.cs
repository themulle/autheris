namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Globalization;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Exact, dialect-independent evaluation of comparisons between two literals. Shared by the simplifier (constant folding)
/// and the unfiltered-DML guard (tautology detection) so both agree on what a literal comparison means.
/// Folding is deliberately conservative: when the outcome could differ between target dialects (collation, implicit coercion,
/// binary floating point) or is three-valued (NULL), no value is produced and the comparison is emitted with its bound literals.
/// </summary>
internal static class LiteralComparison
{
    /// <summary>Digits a decimal can carry and still convert to a double and back without changing equality or order.</summary>
    private const int MaxExactDoubleDigits = 15;

    /// <summary>
    /// Evaluates <c>left op right</c> only when the result is certain in every target dialect:
    /// both numeric (exact decimal semantics, no double rounding), identical strings or two booleans.
    /// Returns false (do not fold) for NULL, mixed type families, non-identical strings and numbers a double cannot represent faithfully.
    /// </summary>
    public static bool TryFold(BinaryOperator op, LiteralExpression left, LiteralExpression right, out bool result)
    {
        result = false;
        if (!IsComparison(op) || left.Type == LiteralType.Null || right.Type == LiteralType.Null)
        {
            return false;
        }

        if (IsNumeric(left) && IsNumeric(right))
        {
            if (left.Value is long l && right.Value is long r)
            {
                return Apply(op, l.CompareTo(r), out result);
            }

            if (TryGetDecimal(left, out var dl) && TryGetDecimal(right, out var dr) &&
                IsDoubleSafe(dl) && IsDoubleSafe(dr))
            {
                return Apply(op, dl.CompareTo(dr), out result);
            }

            return false;
        }

        // Ordering of strings depends on collation. Identity is certain; inequality only when the strings differ even under
        // case-insensitive, trailing-space-insensitive (PAD SPACE) collations, which needs plain ASCII word characters.
        if (left.Type == LiteralType.String && right.Type == LiteralType.String &&
            left.Value is string ls && right.Value is string rs)
        {
            if (string.Equals(ls, rs, StringComparison.Ordinal))
            {
                return Apply(op, 0, out result);
            }

            if (op is BinaryOperator.Equal or BinaryOperator.NotEqual && CertainlyDistinct(ls, rs))
            {
                return Apply(op, 1, out result);
            }

            return false;
        }

        if (left.Type == LiteralType.Boolean && right.Type == LiteralType.Boolean &&
            left.Value is bool lb && right.Value is bool rb && op is BinaryOperator.Equal or BinaryOperator.NotEqual)
        {
            return Apply(op, lb == rb ? 0 : 1, out result);
        }

        return false;
    }

    /// <summary>
    /// Exact numeric comparison of two literals for tautology detection (no size limit; strings are never parsed as numbers
    /// unless the other side is numeric, which keeps mixed comparisons conservative). False when either side is not numeric.
    /// </summary>
    public static bool TryCompareNumeric(LiteralExpression left, LiteralExpression right, out int comparison)
    {
        comparison = 0;
        if (left.Type == LiteralType.Null || right.Type == LiteralType.Null || left.Value == null || right.Value == null)
        {
            return false;
        }

        bool bothText = left.Type == LiteralType.String && right.Type == LiteralType.String;
        if (bothText)
        {
            return false;
        }

        if (TryGetDecimal(left, out var dl) && TryGetDecimal(right, out var dr))
        {
            comparison = dl.CompareTo(dr);
            return true;
        }

        return false;
    }

    private static bool CertainlyDistinct(string a, string b)
    {
        a = a.TrimEnd(' ');
        b = b.TrimEnd(' ');
        return IsAsciiWord(a) && IsAsciiWord(b) && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAsciiWord(string value)
    {
        foreach (char c in value)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or ' '))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsComparison(BinaryOperator op) => op is BinaryOperator.Equal or BinaryOperator.NotEqual or
        BinaryOperator.LessThan or BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThan or BinaryOperator.GreaterThanOrEqual;

    private static bool IsNumeric(LiteralExpression lit) =>
        lit.Type is LiteralType.Integer or LiteralType.Decimal && lit.Value is long or decimal;

    private static bool TryGetDecimal(LiteralExpression lit, out decimal value)
    {
        switch (lit.Value)
        {
            case long l:
                value = l;
                return true;
            case decimal d:
                value = d;
                return true;
            case string s:
                return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            default:
                value = 0;
                return false;
        }
    }

    private static bool IsDoubleSafe(decimal value)
    {
        // Strip trailing zeros (1.00 -> 1), then count significant digits.
        var normalized = value / 1.0000000000000000000000000000m;
        var digits = normalized.ToString(CultureInfo.InvariantCulture).TrimStart('-').Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0');
        return digits.Length <= MaxExactDoubleDigits;
    }

    private static bool Apply(BinaryOperator op, int c, out bool result)
    {
        result = op switch
        {
            BinaryOperator.Equal => c == 0,
            BinaryOperator.NotEqual => c != 0,
            BinaryOperator.LessThan => c < 0,
            BinaryOperator.LessThanOrEqual => c <= 0,
            BinaryOperator.GreaterThan => c > 0,
            _ => c >= 0
        };
        return true;
    }
}
